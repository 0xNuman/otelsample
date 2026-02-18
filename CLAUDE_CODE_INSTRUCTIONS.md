# Claude Code Instructions — .NET 10 Observability Reference App

## Goal
Build a **production-grade reference application** that demonstrates OpenTelemetry-based observability (metrics, traces, logs) in a .NET 10 API using Clean Architecture. This is a learning reference for a SaaS platform migration, so every decision must be explicit, named, and commented to explain *why* it exists.

---

## Constraints & Non-Negotiables
- Target: **.NET 10**, C# 13, latest language features (primary constructors, collection expressions, etc.)
- Architecture: **Controller → Facade → Repository** (Clean Architecture layers)
- ORM: **Dapper** with raw SQL (no EF Core)
- Database: **SQL Server** (LocalDB for local dev, connection string via config)
- Background Jobs: **IHostedService** with a `PeriodicTimer`
- Exporters: dual-mode — **local** (Prometheus scrape + Grafana) AND **AWS** (AMP + X-Ray + CloudWatch)
- Logging: **Serilog** with structured output, wired into OTel
- All observability config must be **environment-switchable** via `appsettings.{Environment}.json`
- Every class, middleware, and extension method must have an **XML doc comment** explaining its observability purpose

---

## Solution Structure

```
OtelSample/
├── OtelSample.sln
├── src/
│   ├── OtelSample.Api/                  # ASP.NET Core host, controllers, middleware
│   ├── OtelSample.Application/          # Facades (use cases), DTOs
│   ├── OtelSample.Infrastructure/       # Repositories, HttpClients, DB
│   └── OtelSample.Observability/        # All OTel wiring, exporters, enrichers
├── docker-compose.yml                   # Local: Prometheus + Grafana + SQL Server
├── grafana/
│   └── dashboards/
│       └── saas-overview.json           # Pre-built Grafana dashboard JSON
└── docs/
    └── observability-guide.md           # Explains every metric, trace, and log choice
```

---

## Step 1 — Scaffold the Solution

```bash
dotnet new sln -n OtelSample
dotnet new webapi -n OtelSample.Api --use-controllers -f net10.0
dotnet new classlib -n OtelSample.Application -f net10.0
dotnet new classlib -n OtelSample.Infrastructure -f net10.0
dotnet new classlib -n OtelSample.Observability -f net10.0
# Add all projects to solution and wire project references
```

Project references:
- `Api` → `Application`, `Infrastructure`, `Observability`
- `Application` → nothing (pure domain)
- `Infrastructure` → `Application`
- `Observability` → `Application` (for tenant context)

---

## Step 2 — NuGet Packages

### OtelSample.Api
```xml
<PackageReference Include="Serilog.AspNetCore" Version="*" />
<PackageReference Include="Serilog.Sinks.Console" Version="*" />
<PackageReference Include="Serilog.Enrichers.Environment" Version="*" />
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="*" />
<PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="*" />
<PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="*" />
<PackageReference Include="OpenTelemetry.Instrumentation.Runtime" Version="*" />
<PackageReference Include="OpenTelemetry.Instrumentation.SqlClient" Version="*" />
<PackageReference Include="OpenTelemetry.Exporter.Prometheus.AspNetCore" Version="*" />
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="*" />
<PackageReference Include="AWS.Distro.OpenTelemetry.AutoInstrumentation" Version="*" />
```

### OtelSample.Observability
```xml
<PackageReference Include="OpenTelemetry" Version="*" />
<PackageReference Include="OpenTelemetry.Api" Version="*" />
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="*" />
```

---

## Step 3 — Tenant Context (Critical — Tag Everything)

### `OtelSample.Application/Context/TenantContext.cs`
```csharp
/// <summary>
/// Ambient tenant context propagated via AsyncLocal.
/// Every OTel span, metric, and log must carry tenant.id as a tag/attribute.
/// Without this, you cannot filter failures or latency spikes per tenant in Grafana or X-Ray.
/// </summary>
public sealed class TenantContext
{
    private static readonly AsyncLocal<string?> _tenantId = new();

    public static string? Current
    {
        get => _tenantId.Value;
        set => _tenantId.Value = value;
    }
}
```

### `OtelSample.Api/Middleware/TenantResolutionMiddleware.cs`
```csharp
/// <summary>
/// Reads X-Tenant-Id header from incoming requests and:
/// 1. Sets TenantContext.Current for AsyncLocal propagation
/// 2. Enriches Activity.Current (OTel span) with tenant.id tag
/// 3. Pushes tenant.id into Serilog LogContext for structured logs
///
/// WHY: A single Activity tag here means every child span (DB calls, HTTP calls)
/// inherits the tag automatically. You never need to pass tenant ID manually.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var tenantId = context.Request.Headers["X-Tenant-Id"].FirstOrDefault() ?? "unknown";

        TenantContext.Current = tenantId;
        Activity.Current?.SetTag("tenant.id", tenantId);

        using (LogContext.PushProperty("TenantId", tenantId))
        {
            await next(context);
        }
    }
}
```

---

## Step 4 — Observability Wiring

### `OtelSample.Observability/OtelConfiguration.cs`

Create a static extension method `AddObservability(this IHostApplicationBuilder builder)` that does the following. Separate each concern into its own private method:

#### 4a. Activity Source (Traces)
```csharp
/// <summary>
/// Custom ActivitySource for application-level traces.
/// WHY: Built-in instrumentation covers HTTP and SQL. This source covers
/// your Facade layer business operations — so you can see what USE CASE
/// was executing when a failure happened, not just which HTTP endpoint.
/// </summary>
public static readonly ActivitySource AppActivitySource = new("OtelSample.App", "1.0.0");
```

#### 4b. Custom Metrics
```csharp
/// <summary>
/// Application-level Meter. Defines all custom metrics.
///
/// METRIC: saas.requests.active — UpDownCounter
///   WHY: Tells you concurrent load at the business operation level, not just HTTP.
///   Spike here = your Facade is backed up, useful when correlating with ThreadPool metrics.
///
/// METRIC: saas.job.processed.total — Counter (tags: job_name, status=success|failure)
///   WHY: Background jobs silently fail with no HTTP status code. This is your
///   only signal that a job failed. Tag with tenant.id for per-tenant job health.
///
/// METRIC: saas.external_http.duration — Histogram (tags: target_service, status_code)
///   WHY: http.client.request.duration from OTel is global. This metric lets you
///   break down latency PER downstream service by name (e.g., "payment-api", "email-api").
///   Essential for pinpointing which dependency is degraded.
///
/// METRIC: saas.db.slow_query.total — Counter (tags: query_name, tenant.id)
///   WHY: SqlClient instrumentation gives you duration. This counter fires only when
///   duration > threshold (e.g., 500ms). In Grafana, alert on this counter rising.
///   Avoids alert fatigue from normal p99 variation.
/// </summary>
```

Implement all four metrics using `System.Diagnostics.Metrics.Meter`.

#### 4c. Trace Configuration
```csharp
builder.Services.AddOpenTelemetry().WithTracing(tracing =>
{
    tracing
        .SetResourceBuilder(ResourceBuilder.CreateDefault()
            .AddService("OtelSample.Api")
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment"] = builder.Environment.EnvironmentName,
                ["service.version"] = Assembly.GetEntryAssembly()!.GetName().Version!.ToString()
            }))
        .AddSource("OtelSample.App")          // Custom app spans
        .AddAspNetCoreInstrumentation(o =>
        {
            // WHY: Record exceptions on spans so X-Ray shows error detail inline
            o.RecordException = true;
            // WHY: Exclude health check noise from traces
            o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
        })
        .AddHttpClientInstrumentation(o =>
        {
            // WHY: Captures outbound HTTP spans — when downstream fails,
            // the span shows status code + URL without any extra code in your HttpClient
            o.RecordException = true;
        })
        .AddSqlClientInstrumentation(o =>
        {
            // WHY: Captures DB span with CommandText so you see the EXACT query
            // that was slow. Do NOT enable in prod if queries contain PII.
            o.SetDbStatementForText = true;
            o.RecordException = true;
        });

    // Exporters — environment-driven
    if (isAwsEnvironment)
    {
        // OTLP → ADOT Collector → X-Ray
        // WHY: X-Ray needs AWSXRayIdGenerator so trace IDs are X-Ray compatible
        tracing
            .AddXRayTraceId()    // AWS.Distro package
            .AddOtlpExporter(o => o.Endpoint = new Uri(config["Otel:OtlpEndpoint"]!));
    }
    else
    {
        // Local: export to console + Jaeger (optional) for dev debugging
        tracing.AddConsoleExporter();
    }
});
```

#### 4d. Metrics Configuration
```csharp
builder.Services.AddOpenTelemetry().WithMetrics(metrics =>
{
    metrics
        .AddMeter("OtelSample.App")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        // Explicit histogram buckets for accurate p99
        // WHY: Default buckets are too coarse for web latency. These boundaries
        // match Prometheus best practices for HTTP services in milliseconds.
        .AddView("http.server.request.duration",
            new ExplicitBucketHistogramConfiguration
            {
                Boundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10]
            })
        .AddView("saas.external_http.duration",
            new ExplicitBucketHistogramConfiguration
            {
                Boundaries = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 30]
            });

    if (isAwsEnvironment)
    {
        // OTLP → ADOT Collector → AMP
        metrics.AddOtlpExporter(o => o.Endpoint = new Uri(config["Otel:OtlpEndpoint"]!));
    }
    else
    {
        // Local: expose /metrics endpoint for Prometheus to scrape
        metrics.AddPrometheusExporter();
    }
});
```

#### 4e. Logging Configuration (Serilog → OTel → CloudWatch)
```csharp
/// <summary>
/// Serilog wiring:
/// WHY Serilog over ILogger directly:
///   - Structured properties (TenantId, TraceId) survive as queryable fields in CloudWatch Insights
///   - Serilog automatically injects TraceId and SpanId from Activity.Current
///     so every log line is correlated to its OTel trace without manual work
///
/// WHY route through OTel sink:
///   - Single ADOT Collector pipeline handles logs just like metrics/traces
///   - Switching from CloudWatch to another backend requires zero app code changes
/// </summary>

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .Enrich.FromLogContext()                          // Picks up TenantId from middleware
    .Enrich.WithMachineName()
    .Enrich.WithEnvironmentName()
    .WriteTo.Console(new JsonFormatter())             // Structured JSON for CloudWatch
    .WriteTo.OpenTelemetry(o =>                       // Route logs through OTel pipeline
    {
        o.Endpoint = isAwsEnvironment
            ? config["Otel:OtlpEndpoint"]!
            : "http://localhost:4317";
        o.ResourceAttributes = new Dictionary<string, object>
        {
            ["service.name"] = "OtelSample.Api"
        };
    })
    .CreateLogger();
```

---

## Step 5 — Clean Architecture Layers

### Controller: `OrdersController`
```csharp
/// <summary>
/// Thin controller. No business logic. No direct DB access.
/// Observability responsibility: none — middleware and OTel auto-instrumentation
/// handle HTTP-level spans and metrics automatically.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class OrdersController(IOrderFacade facade) : ControllerBase
{
    [HttpGet("{id}")]
    public async Task<IActionResult> GetOrder(int id, CancellationToken ct)
    {
        var result = await facade.GetOrderAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> CreateOrder(CreateOrderRequest request, CancellationToken ct)
    {
        var id = await facade.CreateOrderAsync(request, ct);
        return CreatedAtAction(nameof(GetOrder), new { id }, null);
    }
}
```

### Facade: `OrderFacade`
```csharp
/// <summary>
/// Facade (use case orchestrator). This is where business spans live.
/// WHY span here and not in the controller:
///   The controller span is the HTTP span (created by ASP.NET instrumentation).
///   This child span tells you "which USE CASE ran" — critical when one HTTP endpoint
///   triggers multiple business operations. In X-Ray, you'll see:
///   GET /api/orders/{id}
///     └── OrderFacade.GetOrderAsync          ← this span
///           ├── db: SELECT * FROM Orders...  ← SqlClient span (auto)
///           └── http: GET payment-api/...    ← HttpClient span (auto)
/// </summary>
public sealed class OrderFacade(
    IOrderRepository repository,
    IPaymentHttpClient paymentClient,
    ILogger<OrderFacade> logger,
    SaasMetrics metrics) : IOrderFacade
{
    public async Task<OrderDto?> GetOrderAsync(int id, CancellationToken ct)
    {
        using var activity = OtelConfiguration.AppActivitySource
            .StartActivity("OrderFacade.GetOrderAsync");

        activity?.SetTag("order.id", id);
        activity?.SetTag("tenant.id", TenantContext.Current);

        metrics.ActiveRequests.Add(1, new TagList { ["operation"] = "GetOrder" });
        try
        {
            var order = await repository.GetByIdAsync(id, ct);

            if (order is null)
            {
                // WHY log here: 404 is not an exception, won't appear in error spans.
                // Structured log lets you query CloudWatch: "how many 404s per tenant today?"
                logger.LogWarning("Order {OrderId} not found for tenant {TenantId}", id, TenantContext.Current);
                activity?.SetStatus(ActivityStatusCode.Ok, "NotFound");
                return null;
            }

            // Enrich span with outcome for filtering in X-Ray
            activity?.SetTag("order.status", order.Status);
            return order;
        }
        catch (Exception ex)
        {
            // WHY RecordException: writes exception type + message + stack into the span
            // In X-Ray this becomes a searchable "fault" — you can query all traces with this exception type
            activity?.RecordException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex, "Failed to retrieve order {OrderId} for tenant {TenantId}", id, TenantContext.Current);
            throw;
        }
        finally
        {
            metrics.ActiveRequests.Add(-1, new TagList { ["operation"] = "GetOrder" });
        }
    }
}
```

### Repository: `OrderRepository`
```csharp
/// <summary>
/// Dapper repository. SqlClientInstrumentation auto-creates a span per query.
/// This layer adds slow query detection on top.
///
/// WHY slow query counter here:
///   SqlClient span duration is visible in traces but not alertable as a metric.
///   The counter fires when a query exceeds threshold, giving Grafana something to alert on.
///   Tag with query_name (not full SQL) so you can see WHICH query is slow across tenants.
/// </summary>
public sealed class OrderRepository(
    IDbConnectionFactory connectionFactory,
    SaasMetrics metrics,
    ILogger<OrderRepository> logger) : IOrderRepository
{
    private const int SlowQueryThresholdMs = 500;

    public async Task<OrderDto?> GetByIdAsync(int id, CancellationToken ct)
    {
        const string queryName = "Orders.GetById";
        var sw = Stopwatch.StartNew();

        await using var conn = connectionFactory.Create();
        var result = await conn.QuerySingleOrDefaultAsync<OrderDto>(
            "SELECT Id, TenantId, Status, Total FROM Orders WHERE Id = @id AND TenantId = @tenantId",
            new { id, tenantId = TenantContext.Current });

        sw.Stop();

        if (sw.ElapsedMilliseconds > SlowQueryThresholdMs)
        {
            metrics.SlowQueryCounter.Add(1, new TagList
            {
                ["query_name"] = queryName,
                ["tenant.id"] = TenantContext.Current ?? "unknown"
            });

            logger.LogWarning(
                "Slow query detected: {QueryName} took {ElapsedMs}ms for tenant {TenantId}",
                queryName, sw.ElapsedMilliseconds, TenantContext.Current);
        }

        return result;
    }
}
```

---

## Step 6 — External HTTP Client with Observability

### `IPaymentHttpClient` + `PaymentHttpClient`
```csharp
/// <summary>
/// Typed HttpClient for downstream payment service.
/// HttpClientInstrumentation auto-creates spans for every outbound call.
/// This wrapper adds:
///   1. Per-service latency histogram (saas.external_http.duration)
///   2. Structured error logging with correlation to the active trace
///
/// WHY typed client over IHttpClientFactory directly:
///   Typed client enforces the service name tag is always set correctly.
///   Raw IHttpClientFactory calls often forget to tag which service they're calling.
/// </summary>
public sealed class PaymentHttpClient(
    HttpClient httpClient,
    SaasMetrics metrics,
    ILogger<PaymentHttpClient> logger) : IPaymentHttpClient
{
    public async Task<PaymentStatusDto?> GetPaymentStatusAsync(int orderId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string statusCode = "unknown";

        try
        {
            var response = await httpClient.GetAsync($"/payments/{orderId}", ct);
            statusCode = ((int)response.StatusCode).ToString();
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<PaymentStatusDto>(ct);
        }
        catch (HttpRequestException ex)
        {
            // WHY: HttpClient span records the exception, but this log adds business context:
            // which ORDER failed, for which TENANT. Queryable in CloudWatch Insights.
            logger.LogError(ex,
                "Payment service unavailable for order {OrderId}, tenant {TenantId}, status {StatusCode}",
                orderId, TenantContext.Current, statusCode);
            throw;
        }
        finally
        {
            sw.Stop();
            metrics.ExternalHttpDuration.Record(
                sw.Elapsed.TotalSeconds,
                new TagList
                {
                    ["target_service"] = "payment-api",
                    ["http.status_code"] = statusCode,
                    ["tenant.id"] = TenantContext.Current ?? "unknown"
                });
        }
    }
}
```

Register in DI:
```csharp
builder.Services.AddHttpClient<IPaymentHttpClient, PaymentHttpClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Services:PaymentApi:BaseUrl"]!);
    c.Timeout = TimeSpan.FromSeconds(10);
});
```

---

## Step 7 — Background Job with Observability

```csharp
/// <summary>
/// Periodic background job using PeriodicTimer (.NET 6+ idiomatic pattern).
/// WHY observability is harder here:
///   Background jobs have no HTTP request — no automatic OTel span is created.
///   We must manually start a span (Activity) and a Serilog scope.
///   Without this, job failures appear in logs with no TraceId, making
///   correlation to metrics impossible.
///
/// METRIC: saas.job.processed.total
///   Tags: job_name, status (success|failure), tenant.id
///   WHY: Jobs fail silently. This counter is your canary.
///   Alert in Grafana: if failure count > 0 in 5m window → PagerDuty.
/// </summary>
public sealed class OrderSyncJob(
    IServiceScopeFactory scopeFactory,
    SaasMetrics metrics,
    ILogger<OrderSyncJob> logger) : BackgroundService
{
    private readonly PeriodicTimer _timer = new(TimeSpan.FromMinutes(1));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await _timer.WaitForNextTickAsync(stoppingToken))
        {
            await ProcessJobAsync(stoppingToken);
        }
    }

    private async Task ProcessJobAsync(CancellationToken ct)
    {
        // Manually start a root span — jobs have no parent HTTP span
        using var activity = OtelConfiguration.AppActivitySource
            .StartActivity("OrderSyncJob.Process", ActivityKind.Internal);

        var jobId = Guid.NewGuid().ToString();
        activity?.SetTag("job.id", jobId);
        activity?.SetTag("job.name", "OrderSyncJob");

        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["JobId"] = jobId,
            ["JobName"] = "OrderSyncJob"
        });

        logger.LogInformation("OrderSyncJob starting tick");

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var facade = scope.ServiceProvider.GetRequiredService<IOrderFacade>();
            await facade.SyncPendingOrdersAsync(ct);

            metrics.JobProcessedCounter.Add(1, new TagList
            {
                ["job_name"] = "OrderSyncJob",
                ["status"] = "success"
            });

            logger.LogInformation("OrderSyncJob completed successfully");
        }
        catch (Exception ex)
        {
            activity?.RecordException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            metrics.JobProcessedCounter.Add(1, new TagList
            {
                ["job_name"] = "OrderSyncJob",
                ["status"] = "failure"
            });

            logger.LogError(ex, "OrderSyncJob failed on tick {JobId}", jobId);
        }
    }
}
```

---

## Step 8 — Global Exception Handler

```csharp
/// <summary>
/// Catches all unhandled exceptions that escape controller/facade try-catch blocks.
/// WHY this exists separately from RecordException in Facade:
///   Some exceptions (middleware failures, model binding errors, auth failures)
///   never reach the Facade. This handler ensures every 500 response has:
///   1. Exception recorded on the root HTTP span (visible in X-Ray as a fault)
///   2. Structured log with TraceId, TenantId, full exception details
///   3. A ProblemDetails response (RFC 7807) with a CorrelationId the client can report
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) 
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext ctx, Exception ex, CancellationToken ct)
    {
        var traceId = Activity.Current?.TraceId.ToString() ?? ctx.TraceIdentifier;

        Activity.Current?.RecordException(ex);
        Activity.Current?.SetStatus(ActivityStatusCode.Error, ex.Message);

        logger.LogError(ex,
            "Unhandled exception. TraceId: {TraceId}, Tenant: {TenantId}, Path: {Path}",
            traceId, TenantContext.Current, ctx.Request.Path);

        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "An unexpected error occurred",
            Status = 500,
            Extensions = { ["correlationId"] = traceId }
        }, ct);

        return true;
    }
}
```

---

## Step 9 — Health Checks

```csharp
builder.Services.AddHealthChecks()
    .AddSqlServer(connectionString, name: "sql-server", tags: ["db"])
    .AddUrlGroup(new Uri(paymentApiUrl), name: "payment-api", tags: ["external"]);

// Map endpoints
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = hc => hc.Tags.Contains("db") || hc.Tags.Contains("external"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});
```

---

## Step 10 — Docker Compose (Local Stack)

```yaml
# docker-compose.yml
version: '3.9'
services:
  sqlserver:
    image: mcr.microsoft.com/mssql/server:2022-latest
    environment:
      SA_PASSWORD: "YourStrong@Passw0rd"
      ACCEPT_EULA: "Y"
    ports: ["1433:1433"]

  prometheus:
    image: prom/prometheus:latest
    volumes:
      - ./prometheus.yml:/etc/prometheus/prometheus.yml
    ports: ["9090:9090"]
    # prometheus.yml scrape config must target host.docker.internal:5000/metrics

  grafana:
    image: grafana/grafana:latest
    environment:
      GF_SECURITY_ADMIN_PASSWORD: admin
    volumes:
      - ./grafana/dashboards:/var/lib/grafana/dashboards
      - ./grafana/provisioning:/etc/grafana/provisioning
    ports: ["3000:3000"]
    depends_on: [prometheus]

  otel-collector:
    image: otel/opentelemetry-collector-contrib:latest
    command: ["--config=/etc/otel-collector-config.yml"]
    volumes:
      - ./otel-collector-config.yml:/etc/otel-collector-config.yml
    ports:
      - "4317:4317"   # OTLP gRPC
      - "4318:4318"   # OTLP HTTP
```

---

## Step 11 — Grafana Dashboard (saas-overview.json)

Provision a dashboard with these panels. Claude Code should generate the full JSON:

| Panel | Query | Alert |
|-------|-------|-------|
| Request Rate | `rate(http_server_request_duration_count[1m])` | none |
| Error Rate % | `rate(http_server_request_duration_count{http_response_status_code=~"5.."}[1m]) / rate(...total[1m]) * 100` | > 1% |
| p50 / p95 / p99 Latency | `histogram_quantile(0.99, rate(http_server_request_duration_bucket[5m]))` | p99 > 1s |
| Active Requests | `saas_requests_active` | none |
| Slow Query Rate | `rate(saas_db_slow_query_total[5m])` by tenant | > 0 for 2m |
| Job Failure Rate | `rate(saas_job_processed_total{status="failure"}[5m])` | > 0 |
| .NET ThreadPool Queue | `dotnet_threadpool_queue_length` | > 10 sustained |
| External HTTP p99 | `histogram_quantile(0.99, rate(saas_external_http_duration_bucket[5m]))` by target_service | p99 > 2s |
| GC Collections | `rate(dotnet_gc_collections_total[5m])` by generation | gen2 > 1/min |

---

## Step 12 — appsettings Structure

```json
// appsettings.json (base)
{
  "Otel": {
    "ServiceName": "OtelSample.Api",
    "Mode": "local",
    "OtlpEndpoint": "http://localhost:4317",
    "SlowQueryThresholdMs": 500
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "System.Net.Http": "Warning"
      }
    }
  }
}

// appsettings.Production.json
{
  "Otel": {
    "Mode": "aws",
    "OtlpEndpoint": "http://localhost:4317"  // ADOT Collector sidecar on ECS
  }
}
```

---

## Step 13 — Observability Guide (docs/observability-guide.md)

Claude Code must generate this document. It should explain:

1. **How to debug a failed request end-to-end:**
   - Find TraceId in the 500 response `correlationId` field
   - Search X-Ray (AWS) or Jaeger (local) by TraceId
   - Inspect span tree to find which child span failed
   - Click the failed span → see exception type + message + stack
   - Use TraceId to query CloudWatch Insights: `filter TraceId = "abc123"`

2. **How to investigate a slow tenant:**
   - Grafana → Slow Query panel → filter by `tenant.id`
   - Click spike → X-Ray → filter traces by `tenant.id` attribute
   - Look for DB spans with high duration → CommandText shows exact query

3. **How to detect background job degradation:**
   - Grafana → Job Failure Rate panel → alert fires
   - CloudWatch Insights: `filter JobName = "OrderSyncJob" and level = "Error"`
   - Logs include JobId → correlate to span in X-Ray for full context

4. **Scaling signals — what to watch before scaling out:**
   - ThreadPool queue > 0 sustained → CPU bound or sync-over-async blocking
   - Active requests growing without traffic increase → leak or deadlock
   - p99 rising while p50 stable → outlier queries or GC pauses (check GC gen2 panel)

---

## Acceptance Criteria

Claude Code must verify:
- [ ] `dotnet build` completes with 0 errors, 0 warnings
- [ ] `docker-compose up` starts all services successfully
- [ ] `GET /metrics` returns Prometheus-formatted metrics including `saas_*` custom metrics
- [ ] `GET /health/ready` returns 200 with dependency status
- [ ] A request to `GET /api/orders/1` produces a visible trace in console exporter with child spans for DB
- [ ] A simulated 500 error returns a `correlationId` in the response body
- [ ] Slow query (mocked via `Thread.Sleep` or configurable delay) increments `saas_db_slow_query_total`
- [ ] Background job tick produces a root span in console output with `job.name` tag
- [ ] Grafana dashboard loads at `http://localhost:3000` with all panels populated after a few requests

