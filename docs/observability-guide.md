# Observability Guide — OtelSample

This document explains every metric, trace, and log decision in the reference app and provides step-by-step runbooks for common on-call scenarios.

---

## Architecture Overview

```
HTTP Request
  │
  ▼
TenantResolutionMiddleware          ← sets tenant.id on span, AsyncLocal, Serilog
  │
  ▼
OrdersController                    ← thin adapter; HTTP span auto-created by OTel
  │
  ▼
OrderFacade                         ← creates child span; increments ActiveRequests gauge
  ├── OrderRepository                ← Dapper; SqlClient creates DB span automatically
  │     └── saas.db.slow_query.total ← fires if query > 500ms threshold
  └── PaymentHttpClient              ← HttpClient creates outbound span automatically
        └── saas.external_http.duration histogram
  │
  ▼
Response
```

**Three signal types:**

| Signal | Local backend | AWS backend |
|--------|--------------|-------------|
| Traces | Console exporter (stdout) | OTLP → ADOT → X-Ray |
| Metrics | `/metrics` → Prometheus → Grafana | OTLP → ADOT → AMP → Grafana |
| Logs | Console JSON | OTLP → ADOT → CloudWatch Logs |

---

## Signal Inventory

### Traces

| Span name | Created by | Tags |
|-----------|-----------|------|
| `GET /api/orders/{id}` | ASP.NET Core auto-instrumentation | `http.method`, `http.route`, `http.response.status_code`, `tenant.id` |
| `OrderFacade.GetOrderAsync` | Application code (`AppActivitySource`) | `order.id`, `tenant.id`, `order.status` |
| `db.query` (auto) | SqlClient instrumentation | `db.system`, `db.statement`, `db.name` |
| `HTTP GET payment-api/…` (auto) | HttpClient instrumentation | `http.method`, `url.full`, `http.response.status_code` |
| `OrderSyncJob.Process` | Background job code | `job.id`, `job.name` |

### Metrics

| Metric | Type | Tags | Purpose |
|--------|------|------|---------|
| `http.server.request.duration` | Histogram | `http.route`, `http.method`, `http.response.status_code` | Request rate, error rate, latency percentiles |
| `saas.requests.active` | UpDownCounter | `operation` | In-flight Facade operations |
| `saas.db.slow_query.total` | Counter | `query_name`, `tenant.id` | Slow DB queries above threshold |
| `saas.job.processed.total` | Counter | `job_name`, `status` | Job tick outcomes |
| `saas.external_http.duration` | Histogram | `target_service`, `http.status_code`, `tenant.id` | Per-downstream-service latency |
| `dotnet.threadpool.queue.length` | Gauge | — | ThreadPool saturation |
| `dotnet.gc.collections` | Counter | `generation` | GC pressure |
| `dotnet.gc.heap.size` | Gauge | `generation` | Memory usage |

### Structured Log Properties

Every log event carries these Serilog properties:

| Property | Source | Purpose |
|----------|--------|---------|
| `TenantId` | `TenantResolutionMiddleware` → `LogContext.PushProperty` | Per-tenant CloudWatch Insights queries |
| `TraceId` | Serilog OTel sink reads `Activity.Current.TraceId` | Correlate log to trace |
| `SpanId` | Serilog OTel sink reads `Activity.Current.SpanId` | Identify the exact span |
| `MachineName` | `Enrich.WithMachineName()` | Identify pod/host in multi-instance deployments |
| `EnvironmentName` | `Enrich.WithEnvironmentName()` | Distinguish dev/staging/prod in shared log groups |
| `JobId` | `logger.BeginScope(...)` in `OrderSyncJob` | Correlate all log lines from one job tick |

---

## Runbook 1: Debug a Failed Request End-to-End

**Symptom:** A user reports a 500 error. They provide the request timestamp and their tenant ID.

**Step 1 — Get the correlation ID**

The response body contains:
```json
{ "correlationId": "7f4a2b1c00000000a3e5f1d2..." }
```
This is the W3C TraceId (32 hex chars). Copy it.

**Step 2 — Find the trace**

*Local:* Check the console exporter output. Search for `TraceId: 7f4a2b1c...`

*AWS:* Go to **AWS X-Ray → Traces**, paste the Trace ID into the search box. You will see the full flame graph.

**Step 3 — Identify the failing span**

Look for spans with a red error indicator. Click the span to see:
- `error.type` — the exception class
- `error.message` — the exception message
- Stack trace (recorded by `activity.RecordException(ex)`)

**Step 4 — Correlate to logs**

*AWS CloudWatch Insights:*
```sql
filter TraceId = "7f4a2b1c00000000a3e5f1d2..."
| sort @timestamp asc
```

This returns every log event from that exact request, including the tenant ID, path, and any structured context.

**Step 5 — Reproduce**

Use the `TenantId` from the log to send a request with `X-Tenant-Id: <value>` and reproduce the failure in a lower environment.

---

## Runbook 2: Investigate a Slow Tenant

**Symptom:** Grafana alert fires on `saas.db.slow_query.total` for tenant `acme-corp`.

**Step 1 — Grafana: Slow Query panel**

Open the **Slow Query Rate** panel. Use the legend filter to show only `tenant_id="acme-corp"`. Identify which `query_name` is spiking (e.g., `Orders.GetById`).

**Step 2 — X-Ray: Find slow traces**

Filter by `tenant.id = "acme-corp"` AND response time > 1s. Click into a slow trace and expand the span tree. Find the DB span with the highest duration.

**Step 3 — Read the query**

The `db.statement` attribute on the DB span shows the exact SQL being executed. This is captured by `SetDbStatementForText = true` in `OtelConfiguration`.

**Step 4 — Cross-check with External HTTP**

Check the **External HTTP p99** panel filtered by `tenant.id`. If `payment-api` is also slow for this tenant, the slowness is in the downstream service, not the DB.

**Step 5 — Check runtime metrics**

If neither DB nor external HTTP is slow but the Facade span is, check:
- `dotnet.threadpool.queue.length` — if > 0, the ThreadPool is saturated
- GC Gen 2 rate — if elevated, a GC pause may be causing latency spikes

---

## Runbook 3: Background Job Degradation

**Symptom:** The `Background Job Failures` alert fires in Grafana.

**Step 1 — Grafana: Job Failure Rate panel**

Confirm the failure rate is rising. Note the `job_name` tag.

**Step 2 — CloudWatch Insights**

```sql
filter JobName = "OrderSyncJob"
  and level = "Error"
  and @timestamp > now() - 30m
| sort @timestamp desc
```

Each failure log includes:
- `JobId` — unique per tick
- The exception message
- `TraceId` — even though there's no HTTP request, the job manually starts an Activity

**Step 3 — X-Ray by JobId**

The `job.id` span tag is set in `OrderSyncJob`. Search X-Ray for traces with this attribute. You'll see the full span tree for the failing tick, including which downstream call failed.

**Step 4 — Check dependency health**

Look at the **External HTTP p99** panel around the time of failure. If `payment-api` shows elevated error rates, the job is failing because the downstream service is down.

---

## Runbook 4: Scaling Signals — When to Scale Out

Monitor these metrics before scaling horizontally:

| Signal | Threshold | Interpretation |
|--------|-----------|----------------|
| `dotnet.threadpool.queue.length > 0` sustained | 5 minutes | CPU-bound or sync-over-async blocking. Adding instances splits load but doesn't fix the root cause. Profile first. |
| `saas.requests.active` growing without traffic increase | — | Facade operations are getting stuck. Likely a DB lock, downstream timeout, or deadlock. Adding instances makes it worse. |
| `http.server.request.duration` p99 rising, p50 stable | — | Outlier requests are skewing p99. Check GC Gen 2 rate — a collection pause affects a small fraction of requests. |
| `http.server.request.duration` p50 AND p99 rising together | — | Systemic slowdown. Check DB latency first, then external HTTP, then runtime. |
| GC Gen 2 > 1 per minute | sustained | Large object heap pressure. Profile with `dotnet-trace` before scaling. |

**Correct scaling trigger:** CPU utilisation > 70% OR `saas.requests.active` growing proportionally with traffic AND p99 is acceptable.

---

## Local Development Quick Start

```bash
# 1. Start the infrastructure stack
podman compose up -d

# 2. Run the API
cd src/OtelSample.Api
dotnet run

# 3. Send a request (use X-Tenant-Id to see tenant tagging)
curl -H "X-Tenant-Id: tenant-a" http://localhost:5000/api/orders/1

# 4. View metrics
curl http://localhost:5000/metrics | grep saas_

# 5. Check health
curl http://localhost:5000/health/ready

# 6. Trigger the error handler (check correlationId in response)
curl http://localhost:5000/api/orders/error-test

# 7. Open Grafana
open http://localhost:3000  # admin / admin
```

Trace output appears in the terminal running `dotnet run` (Console exporter).
The background job fires every minute — watch for `OrderSyncJob tick starting` log lines.

---

## AWS Deployment Checklist

- [ ] Set `ASPNETCORE_ENVIRONMENT=Production` (switches Otel:Mode to "aws")
- [ ] Deploy ADOT Collector as a sidecar on the same ECS task definition, listening on `localhost:4317`
- [ ] Set `ConnectionStrings__SqlServer` as a Secrets Manager secret injected via ECS task definition
- [ ] Create an AMP workspace and configure the ADOT Collector `prometheusremotewrite` exporter
- [ ] Create an X-Ray group with filter `annotation.service = "OtelSample.Api"` for cost control
- [ ] Create CloudWatch log group `/otelsample/api` and configure the ADOT `awscloudwatchlogs` exporter
- [ ] Import the `grafana/dashboards/saas-overview.json` into your managed Grafana (AMG) instance
- [ ] Set AMP as the Grafana data source (use IAM role for authentication)
