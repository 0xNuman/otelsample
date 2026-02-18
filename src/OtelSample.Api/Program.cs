using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OtelSample.Api.Extensions;
using OtelSample.Api.Middleware;
using OtelSample.Infrastructure.Facades;
using OtelSample.Application.Interfaces;
using OtelSample.Infrastructure.Data;
using OtelSample.Infrastructure.Http;
using OtelSample.Infrastructure.Jobs;
using OtelSample.Observability;
using Serilog;

// ──────────────────────────────────────────────────────────────────────────────
// Bootstrap Serilog early so startup errors are captured in structured format.
// The full configuration (OTel sink, enrichers) is applied inside AddObservability.
// ──────────────────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ── Observability (traces, metrics, Serilog) ─────────────────────────────
    // Must be called before any other service registration so the logger is
    // available to services registered below.
    builder.AddObservability();

    // ── Application services ─────────────────────────────────────────────────
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    // ── Exception handling ───────────────────────────────────────────────────
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddProblemDetails();

    // ── Health checks ─────────────────────────────────────────────────────────
    // WHY two endpoints:
    //   /health/live  — liveness: is the process alive? No dependency checks.
    //                   Load balancer uses this to decide whether to restart the pod.
    //   /health/ready — readiness: are all dependencies healthy?
    //                   Load balancer uses this to decide whether to route traffic.
    var connectionString = builder.Configuration.GetConnectionString("SqlServer")!;
    var paymentApiUrl = builder.Configuration["Services:PaymentApi:BaseUrl"] ?? "http://localhost:9999";

    builder.Services.AddHealthChecks()
        .AddSqlServer(
            connectionString,
            name: "sql-server",
            tags: ["db"])
        .AddUrlGroup(
            new Uri($"{paymentApiUrl}/health"),
            name: "payment-api",
            tags: ["external"]);

    // ── Dependency Injection — Infrastructure ─────────────────────────────────
    // WHY Singleton for connection factory: SqlConnectionFactory holds no per-request
    // state — it only reads the connection string from IConfiguration and creates new
    // SqlConnection objects on each call. Singleton avoids repeated config lookups and
    // matches the lifetime of DbInitialiser (which runs once at startup).
    builder.Services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();
    builder.Services.AddScoped<IOrderRepository, OrderRepository>();
    // WHY Singleton for DbInitialiser: it is called exactly once at startup (not per
    // request), so Singleton is the correct lifetime. With IDbConnectionFactory also
    // Singleton there is no captive-dependency violation.
    builder.Services.AddSingleton<DbInitialiser>();

    // Typed HttpClient for the payment service.
    // WHY AddHttpClient<TClient, TImpl>: DI manages HttpMessageHandler lifetime,
    // preventing socket exhaustion from using `new HttpClient()` directly.
    builder.Services.AddHttpClient<IPaymentHttpClient, PaymentHttpClient>(c =>
    {
        c.BaseAddress = new Uri(builder.Configuration["Services:PaymentApi:BaseUrl"]
                                ?? "http://localhost:9999");
        c.Timeout = TimeSpan.FromSeconds(10);
    });

    // ── Dependency Injection — Application ────────────────────────────────────
    builder.Services.AddScoped<IOrderFacade, OrderFacade>();

    // ── Background job ────────────────────────────────────────────────────────
    builder.Services.AddHostedService<OrderSyncJob>();

    // ─────────────────────────────────────────────────────────────────────────
    var app = builder.Build();
    // ─────────────────────────────────────────────────────────────────────────

    // ── Initialise database schema ────────────────────────────────────────────
    await app.Services.GetRequiredService<DbInitialiser>().InitialiseAsync();

    // ── Middleware pipeline ───────────────────────────────────────────────────
    // Order matters: ExceptionHandler must be first so it catches exceptions from
    // all subsequent middleware. Tenant resolution must come before routing so the
    // tenant ID is available in controllers.
    app.UseExceptionHandler();
    app.UseMiddleware<TenantResolutionMiddleware>();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseRouting();

    // ── Health check endpoints ────────────────────────────────────────────────
    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        // Liveness: always healthy as long as the process responds.
        Predicate = _ => false
    });

    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = hc => hc.Tags.Contains("db") || hc.Tags.Contains("external"),
        // WHY UIResponseWriter: returns a detailed JSON body showing each check's
        // status, duration, and description — useful for debugging dependency failures.
        ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
    });

    app.MapControllers();

    // ── Observability endpoints ───────────────────────────────────────────────
    // Maps /metrics for Prometheus in local mode; no-op in AWS mode.
    app.MapObservabilityEndpoints();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Application startup failed");
    return 1;
}
finally
{
    // Flush all buffered log events before the process exits.
    await Log.CloseAndFlushAsync();
}

return 0;
