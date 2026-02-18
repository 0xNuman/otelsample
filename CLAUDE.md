# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

OtelSample is a .NET 10 reference application demonstrating OpenTelemetry-based observability (metrics, traces, logs) in a Clean Architecture API. It uses Dapper with raw SQL against SQL Server, Serilog for structured logging, and supports dual-mode exporters (local Prometheus/Grafana + AWS X-Ray/AMP/CloudWatch). The design instructions live in `CLAUDE_CODE_INSTRUCTIONS.md`.

## Build & Run

```bash
# Build (from repo root)
dotnet build

# Start local infrastructure (SQL Server, Prometheus, Grafana, OTel Collector)
podman compose up -d    # or docker compose up -d

# Run the API (host-based, not containerised)
dotnet run --project src/OtelSample.Api

# Test endpoints
curl -H "X-Tenant-Id: tenant-a" http://localhost:5000/api/orders/1
curl http://localhost:5000/metrics | grep saas_
curl http://localhost:5000/health/ready
curl http://localhost:5000/api/orders/error-test   # deliberate 500

# Grafana: http://localhost:3000 (admin/admin)
# Swagger: http://localhost:5000/swagger (Development only)
```

No test projects exist yet. The solution file is `OtelSample.slnx` (modern XML format).

## Architecture

**Clean Architecture layers** with flow: Controller → Facade → Repository.

```
src/
├── OtelSample.Api/            # ASP.NET Core host, controllers, middleware, Program.cs
├── OtelSample.Application/    # Pure domain: DTOs, interfaces, TenantContext (no dependencies)
├── OtelSample.Infrastructure/ # Facades, repositories (Dapper), typed HTTP clients, background jobs
└── OtelSample.Observability/  # All OTel wiring: OtelConfiguration.cs (~700 lines), SaasMetrics.cs
```

**Project references:** Api → Application, Infrastructure, Observability | Infrastructure → Application, Observability | Observability → Application | Application → nothing

**DI lifetimes (Program.cs):** `IDbConnectionFactory` and `DbInitialiser` are Singleton (stateless). `IOrderRepository` and `IOrderFacade` are Scoped. `IPaymentHttpClient` is Transient via `AddHttpClient<>`.

## Key Observability Patterns

- **Tenant propagation:** `TenantResolutionMiddleware` sets `TenantContext.Current` (AsyncLocal), tags `Activity.Current`, and pushes to Serilog LogContext — all child spans inherit `tenant.id` automatically.
- **Business spans:** Facades create child Activities via `OtelConfiguration.AppActivitySource` to separate business operations from HTTP-level spans.
- **Mode switching:** `Otel:Mode` in appsettings (`local`/`aws`) switches exporters without recompilation. Production overrides in `appsettings.Production.json`.
- **Background jobs:** `OrderSyncJob` manually starts Activities and Serilog scopes since there's no HTTP context.
- **Error handling:** `GlobalExceptionHandler` records exceptions on spans, logs with TraceId/TenantId, returns RFC 7807 ProblemDetails with `correlationId`.

## Custom Metrics (SaasMetrics.cs)

| Metric | Type | Purpose |
|--------|------|---------|
| `saas.requests.active` | UpDownCounter | In-flight facade operations |
| `saas.db.slow_query.total` | Counter | Queries exceeding threshold (default 500ms, dev 100ms) |
| `saas.external_http.duration` | Histogram | Per-downstream-service latency |
| `saas.job.processed.total` | Counter | Background job outcomes (success/failure) |

## Coding Conventions

- **.NET 10 / C# 13:** primary constructors for DI, collection expressions, raw string literals, nullable reference types enabled
- **XML doc comments** on every class/method explaining *why* it exists (observability purpose), not just *what*
- **Structured logging:** always use message templates with named properties (`{OrderId}`, `{TenantId}`) — these become queryable fields in CloudWatch Insights
- **Slow query detection:** repositories use `Stopwatch` + counter pattern, not histogram p99
- All OpenTelemetry packages pinned to **1.15.0**

## Docker Stack

`docker-compose.yml` runs: SQL Server (port 1433, sa/YourStrong@Passw0rd), Prometheus (9090), Grafana (3000), OTel Collector (4317/4318). The .NET app runs on the host — Prometheus scrapes `host.docker.internal:5000/metrics`. SQL Server image uses `linux/arm64` platform for Apple Silicon.

## Runbooks

`docs/observability-guide.md` contains four on-call runbooks: debug failed request end-to-end, investigate slow tenant, background job degradation, and scaling signals.
