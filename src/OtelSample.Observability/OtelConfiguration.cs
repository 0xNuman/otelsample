using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Json;

namespace OtelSample.Observability;

/// <summary>
/// Central wiring point for all OpenTelemetry instrumentation.
///
/// Design decisions:
///   - One extension method (<see cref="AddObservability"/>) keeps Program.cs clean.
///   - Each signal (traces, metrics, logging) is configured in its own private method
///     so diffs are localised and reviewers can understand each concern independently.
///   - Mode switching (local vs AWS) is driven by <c>Otel:Mode</c> in appsettings,
///     so no recompilation is needed when deploying to AWS.
///
/// Local mode  → Prometheus scrape endpoint (/metrics) + Console trace exporter
/// AWS mode    → OTLP → ADOT Collector sidecar → X-Ray / AMP / CloudWatch
/// </summary>
public static class OtelConfiguration
{
    /// <summary>
    /// Shared <see cref="ActivitySource"/> used by Facades and background jobs to
    /// emit application-level spans.
    ///
    /// WHY a custom ActivitySource:
    ///   ASP.NET Core instrumentation creates one span per HTTP request; SqlClient creates
    ///   one span per query. Neither tells you WHICH business operation was running.
    ///   This source fills that gap: every Facade method starts a child Activity, so the
    ///   X-Ray / Jaeger flame graph shows the full call tree with business context.
    /// </summary>
    public static readonly ActivitySource AppActivitySource = new("OtelSample.App", "1.0.0");

    /// <summary>
    /// Registers OpenTelemetry tracing, metrics, and Serilog structured logging.
    /// Call from <c>Program.cs</c> before <c>builder.Build()</c>.
    /// </summary>
    public static IHostApplicationBuilder AddObservability(this IHostApplicationBuilder builder)
    {
        var config = builder.Configuration;
        var isAwsMode = string.Equals(
            config["Otel:Mode"], "aws", StringComparison.OrdinalIgnoreCase);

        ConfigureTracing(builder, config, isAwsMode);
        ConfigureMetrics(builder, config, isAwsMode);
        ConfigureLogging(builder, config, isAwsMode);

        // Register SaasMetrics as singleton so all injection sites share one Meter instance.
        builder.Services.AddSingleton<SaasMetrics>();

        return builder;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Tracing
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Configures the OpenTelemetry TracerProvider.
    ///
    /// Instrumentation layers:
    ///   • ASP.NET Core — one span per HTTP request, exceptions recorded inline.
    ///     Health-check paths are excluded to avoid trace noise.
    ///   • HttpClient — one span per outbound HTTP call, status code visible in span.
    ///   • SqlClient — one span per SQL command; CommandText captured for debugging.
    ///     WARNING: Disable <see cref="SqlClientTraceInstrumentationOptions.SetDbStatementForText"/>
    ///     in production if SQL contains PII.
    ///   • AppActivitySource — business-level spans from Facades and background jobs.
    ///
    /// AWS mode adds:
    ///   • AWSXRayIdGenerator — rewrites trace IDs into X-Ray's epoch-prefixed format
    ///     so AWS Console can link traces to CloudWatch log entries by time.
    ///   • OTLP exporter targeting the ADOT Collector sidecar.
    ///
    /// Local mode uses the Console exporter so you can see trace output without
    /// running any collector infrastructure.
    /// </summary>
    private static void ConfigureTracing(
        IHostApplicationBuilder builder,
        IConfiguration config,
        bool isAwsMode)
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

        builder.Services.AddOpenTelemetry().WithTracing(tracing =>
        {
            tracing
                .SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddService(
                        serviceName: config["Otel:ServiceName"] ?? "OtelSample.Api",
                        serviceVersion: version)
                    .AddAttributes(new Dictionary<string, object>
                    {
                        ["deployment.environment"] = builder.Environment.EnvironmentName,
                        ["service.version"] = version
                    }))
                .AddSource(AppActivitySource.Name)
                .AddAspNetCoreInstrumentation(o =>
                {
                    // WHY RecordException: exceptions are written into the span's event list,
                    // making them searchable as "faults" in X-Ray and Jaeger without any
                    // extra logging — the span IS the error record.
                    o.RecordException = true;

                    // WHY filter health checks: liveness probes fire every few seconds from
                    // the load balancer. Including them drowns real traces in noise and
                    // inflates sampling costs.
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
                })
                .AddHttpClientInstrumentation(o =>
                {
                    // WHY RecordException: HttpRequestException carries the status code, which
                    // is then visible on the span — no need to log it separately.
                    o.RecordException = true;
                })
                .AddSqlClientInstrumentation(o =>
                {
                    // WHY RecordException: SqlException details (error code, server name)
                    // appear in the span event list, making DB failures searchable in X-Ray.
                    //
                    // NOTE on query text capture (OTel SqlClient 1.x stable):
                    //   SetDbStatementForText was removed in the 1.x stable API.
                    //   Query text is now captured via environment variable:
                    //     OTEL_DOTNET_EXPERIMENTAL_SQLCLIENT_ENABLE_TRACE_DB_STATEMENT=true
                    //   Set this env var in appsettings or your container task definition.
                    //   Do NOT enable in production if queries may contain PII.
                    o.RecordException = true;
                });

            if (isAwsMode)
            {
                // WHY AWSXRayIdGenerator:
                //   X-Ray trace IDs embed a Unix timestamp in the first 8 hex digits.
                //   Without this generator, W3C trace IDs are random and X-Ray cannot
                //   correlate traces to approximate time ranges, breaking the "Search by
                //   time" workflow in the AWS Console.
                tracing
                    .AddXRayTraceId()   // OpenTelemetry.Extensions.AWS — rewrites W3C trace IDs
                    .AddOtlpExporter(o =>
                    {
                        o.Endpoint = new Uri(config["Otel:OtlpEndpoint"]!);
                        o.Protocol = OtlpExportProtocol.Grpc;
                    });
            }
            else
            {
                // Local dev: structured console output lets you see the full span tree
                // in the terminal without running Jaeger.
                tracing.AddConsoleExporter();
            }
        });
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Metrics
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Configures the OpenTelemetry MeterProvider.
    ///
    /// Custom histogram views override the default bucket boundaries:
    ///   • http.server.request.duration — fine-grained web latency buckets (ms range).
    ///     WHY: OTel defaults (1s, 5s, …) are designed for RPC, not sub-millisecond web APIs.
    ///     Without explicit boundaries, p99 estimates are too coarse to act on.
    ///   • saas.external_http.duration — wider buckets for downstream services that may
    ///     legitimately take seconds (payment processing, third-party APIs).
    ///
    /// Local mode: Prometheus pull model via /metrics endpoint.
    /// AWS mode:   OTLP push to ADOT Collector → Amazon Managed Prometheus.
    /// </summary>
    private static void ConfigureMetrics(
        IHostApplicationBuilder builder,
        IConfiguration config,
        bool isAwsMode)
    {
        builder.Services.AddOpenTelemetry().WithMetrics(metrics =>
        {
            metrics
                .SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddService(config["Otel:ServiceName"] ?? "OtelSample.Api"))
                .AddMeter(SaasMetrics.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                // Prometheus best-practice latency buckets for HTTP services (values in seconds).
                // WHY these specific values: they give ≤2× resolution across the 5ms–10s range,
                // which is wide enough for API timeouts but fine enough to distinguish p50/p95/p99.
                .AddView("http.server.request.duration",
                    new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10]
                    })
                // Wider buckets for downstream services (payment processing can take seconds).
                .AddView("saas.external_http.duration",
                    new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 30]
                    });

            if (isAwsMode)
            {
                metrics.AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri(config["Otel:OtlpEndpoint"]!);
                    o.Protocol = OtlpExportProtocol.Grpc;
                });
            }
            else
            {
                // WHY Prometheus pull: Prometheus scrapes /metrics on its own schedule,
                // which means the app has no knowledge of the scrape interval and cannot
                // accidentally flood the collector. Prometheus also provides local storage
                // for Grafana without any cloud dependency.
                metrics.AddPrometheusExporter();
            }
        });
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Logging
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Configures Serilog as the logging backend and wires it into the OTel pipeline.
    ///
    /// WHY Serilog over Microsoft.Extensions.Logging directly:
    ///   • Structured properties (TenantId, TraceId) become first-class queryable fields
    ///     in CloudWatch Insights — you can write <c>filter TenantId = "acme"</c>.
    ///   • Serilog reads Activity.Current automatically, injecting TraceId and SpanId
    ///     into every log event with zero manual effort.
    ///   • Sink composition: Console (for local dev) + OTel sink (for cloud) share the
    ///     same enricher pipeline; adding a new sink requires one line of code.
    ///
    /// WHY route logs through OTel sink:
    ///   The ADOT Collector becomes the single egress point for all three signals
    ///   (traces, metrics, logs). Switching from CloudWatch to Grafana Loki requires
    ///   only an ADOT Collector config change — zero application code changes.
    /// </summary>
    private static void ConfigureLogging(
        IHostApplicationBuilder builder,
        IConfiguration config,
        bool isAwsMode)
    {
        var otlpEndpoint = isAwsMode
            ? config["Otel:OtlpEndpoint"]!
            : "http://localhost:4317";

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(config)
            .Enrich.FromLogContext()        // Picks up TenantId pushed by TenantResolutionMiddleware
            .Enrich.WithMachineName()
            .Enrich.WithEnvironmentName()
            .WriteTo.Console(new JsonFormatter())   // Structured JSON — parseable by CloudWatch agent
            .WriteTo.OpenTelemetry(o =>
            {
                // Route logs through OTel OTLP so the same collector pipeline handles all signals.
                o.Endpoint = otlpEndpoint;
                o.ResourceAttributes = new Dictionary<string, object>
                {
                    ["service.name"] = config["Otel:ServiceName"] ?? "OtelSample.Api"
                };
            })
            .CreateLogger();

        builder.Logging.AddSerilog(Log.Logger, dispose: true);
    }

}
