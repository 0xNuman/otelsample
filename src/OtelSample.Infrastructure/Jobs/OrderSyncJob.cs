using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OtelSample.Application.Interfaces;
using OtelSample.Observability;

namespace OtelSample.Infrastructure.Jobs;

/// <summary>
/// Periodic background service that synchronises pending orders with the payment service.
///
/// WHY observability is harder for background jobs:
///   HTTP requests get an OTel span automatically via ASP.NET Core instrumentation.
///   Background jobs have no HTTP context — no span is created unless you start one manually.
///   Without a manual span, job failures produce log lines with no TraceId, making it
///   impossible to correlate a log message to a metric spike in Grafana.
///
/// This job demonstrates the correct pattern:
///   1. Start a root Activity (span) at the top of each tick.
///   2. Open a Serilog scope with the JobId so every log line in the tick carries it.
///   3. Emit a job outcome counter (success|failure) so Grafana can alert on failures.
///
/// WHY PeriodicTimer over Timer or Task.Delay loop:
///   PeriodicTimer skips missed ticks (if a tick takes longer than the period) and
///   is cancellation-token aware, eliminating the need for manual CancellationToken
///   plumbing that Timer-based approaches require.
/// </summary>
public sealed class OrderSyncJob(
    IServiceScopeFactory scopeFactory,
    SaasMetrics metrics,
    ILogger<OrderSyncJob> logger) : BackgroundService
{
    private readonly PeriodicTimer _timer = new(TimeSpan.FromMinutes(1));

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run one tick immediately on startup so there is observable output without
        // waiting a full minute in the reference app.
        await ProcessJobAsync(stoppingToken);

        while (await _timer.WaitForNextTickAsync(stoppingToken))
        {
            await ProcessJobAsync(stoppingToken);
        }
    }

    private async Task ProcessJobAsync(CancellationToken ct)
    {
        // ActivityKind.Internal: this job does not participate in distributed tracing
        // as a server or client — it is a self-contained internal process.
        // WHY not ActivityKind.Producer: there is no corresponding Consumer span; the
        // job calls services directly rather than publishing to a queue.
        using var activity = OtelConfiguration.AppActivitySource
            .StartActivity("OrderSyncJob.Process", ActivityKind.Internal);

        var jobId = Guid.NewGuid().ToString();
        activity?.SetTag("job.id", jobId);
        activity?.SetTag("job.name", "OrderSyncJob");

        // WHY BeginScope: pushes JobId and JobName into every log event emitted
        // during this tick. In CloudWatch Insights you can filter by JobId to see
        // every log line from a single job execution.
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["JobId"] = jobId,
            ["JobName"] = "OrderSyncJob"
        });

        logger.LogInformation("OrderSyncJob tick starting");

        try
        {
            // WHY CreateAsyncScope: BackgroundService has a singleton lifetime;
            // IOrderFacade and IOrderRepository are scoped. Using CreateAsyncScope
            // gives each tick its own DI scope, preventing scoped services from
            // leaking state between ticks.
            await using var scope = scopeFactory.CreateAsyncScope();
            var facade = scope.ServiceProvider.GetRequiredService<IOrderFacade>();
            await facade.SyncPendingOrdersAsync(ct);

            metrics.JobProcessedCounter.Add(1, new TagList
            {
                { "job_name", "OrderSyncJob" },
                { "status", "success" }
            });

            activity?.SetStatus(ActivityStatusCode.Ok);
            logger.LogInformation("OrderSyncJob tick completed successfully");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown — do not record as a failure.
            logger.LogInformation("OrderSyncJob tick cancelled due to shutdown");
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            metrics.JobProcessedCounter.Add(1, new TagList
            {
                { "job_name", "OrderSyncJob" },
                { "status", "failure" }
            });

            // WHY log and swallow: an unhandled exception in ExecuteAsync stops the
            // BackgroundService permanently. Swallowing here lets the next tick retry.
            // The metric counter is your alerting signal.
            logger.LogError(ex, "OrderSyncJob tick failed (JobId: {JobId})", jobId);
        }
    }
}
