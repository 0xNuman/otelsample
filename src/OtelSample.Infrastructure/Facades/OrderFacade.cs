using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using OtelSample.Application.Context;
using OtelSample.Application.DTOs;
using OtelSample.Application.Interfaces;
using OtelSample.Observability;

namespace OtelSample.Infrastructure.Facades;

/// <summary>
/// Orchestrates order use cases. This is the boundary between the HTTP layer
/// (Controllers) and the data/infrastructure layer (Repositories, HTTP clients).
///
/// WHY spans live here and not in the Controller:
///   ASP.NET Core instrumentation already creates a span for the HTTP request.
///   This Facade span is a CHILD of that HTTP span. In X-Ray / Jaeger you see:
///     GET /api/orders/{id}                    ← ASP.NET Core span
///       └── OrderFacade.GetOrderAsync         ← this span
///             ├── db: SELECT * FROM Orders…   ← SqlClient span (auto)
///             └── http: GET payment-api/…     ← HttpClient span (auto)
///
///   This tree answers "which use case was running when the failure occurred?",
///   which is impossible to answer from HTTP spans alone when an endpoint
///   delegates to multiple business operations.
/// </summary>
public sealed class OrderFacade(
    IOrderRepository repository,
    IPaymentHttpClient paymentClient,
    ILogger<OrderFacade> logger,
    SaasMetrics metrics) : IOrderFacade
{
    /// <inheritdoc/>
    public async Task<OrderDto?> GetOrderAsync(int id, CancellationToken ct = default)
    {
        // Start a child Activity. If no HTTP span is active (e.g. in tests), this
        // becomes the root span. Using "using" ensures the span is ended even on exception.
        using var activity = OtelConfiguration.AppActivitySource
            .StartActivity("OrderFacade.GetOrderAsync");

        activity?.SetTag("order.id", id);
        activity?.SetTag("tenant.id", TenantContext.Current);

        // Increment the active-request gauge for this operation.
        // WHY: lets Grafana show "how many GetOrder calls are in flight right now?"
        // A spike here without a traffic spike = Facade is stuck (DB lock, downstream timeout).
        metrics.ActiveRequests.Add(1, new TagList { { "operation", "GetOrder" } });
        try
        {
            var order = await repository.GetByIdAsync(id, ct);

            if (order is null)
            {
                // WHY log here: a 404 is not an exception, so it won't appear on the span
                // as an error event. This log lets you query CloudWatch Insights:
                // "how many 404s per tenant today?" to detect stale client references.
                logger.LogWarning(
                    "Order {OrderId} not found for tenant {TenantId}",
                    id, TenantContext.Current);

                activity?.SetStatus(ActivityStatusCode.Ok, "NotFound");
                return null;
            }

            // Enrich span with order outcome — useful for filtering X-Ray traces by status.
            activity?.SetTag("order.status", order.Status);
            return order;
        }
        catch (Exception ex)
        {
            // WHY RecordException: writes the exception type, message, and stack trace into
            // the span as a structured event. In X-Ray this becomes a searchable "fault".
            // You can query: "show all traces that had an InvalidOperationException".
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex,
                "Failed to retrieve order {OrderId} for tenant {TenantId}",
                id, TenantContext.Current);
            throw;
        }
        finally
        {
            // Always decrement — even on exception — to keep the gauge accurate.
            metrics.ActiveRequests.Add(-1, new TagList { { "operation", "GetOrder" } });
        }
    }

    /// <inheritdoc/>
    public async Task<int> CreateOrderAsync(CreateOrderRequest request, CancellationToken ct = default)
    {
        using var activity = OtelConfiguration.AppActivitySource
            .StartActivity("OrderFacade.CreateOrderAsync");

        var tenantId = TenantContext.Current ?? "unknown";
        activity?.SetTag("tenant.id", tenantId);
        activity?.SetTag("order.total", request.Total);

        metrics.ActiveRequests.Add(1, new TagList { { "operation", "CreateOrder" } });
        try
        {
            var id = await repository.CreateAsync(tenantId, request.Total, ct);
            activity?.SetTag("order.id", id);
            logger.LogInformation(
                "Created order {OrderId} for tenant {TenantId} with total {Total}",
                id, tenantId, request.Total);
            return id;
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex,
                "Failed to create order for tenant {TenantId}", tenantId);
            throw;
        }
        finally
        {
            metrics.ActiveRequests.Add(-1, new TagList { { "operation", "CreateOrder" } });
        }
    }

    /// <inheritdoc/>
    public async Task SyncPendingOrdersAsync(CancellationToken ct = default)
    {
        using var activity = OtelConfiguration.AppActivitySource
            .StartActivity("OrderFacade.SyncPendingOrdersAsync");

        activity?.SetTag("tenant.id", TenantContext.Current);

        var pending = await repository.GetPendingAsync(ct);
        activity?.SetTag("orders.pending_count", pending.Count);

        logger.LogInformation("Syncing {Count} pending orders", pending.Count);

        foreach (var order in pending)
        {
            try
            {
                // Fetch payment status for each pending order — demonstrates HttpClient
                // instrumentation producing child spans automatically.
                var payment = await paymentClient.GetPaymentStatusAsync(order.Id, ct);
                await repository.MarkSyncedAsync(order.Id, ct);

                logger.LogInformation(
                    "Order {OrderId} synced, payment status: {PaymentStatus}",
                    order.Id, payment?.Status ?? "unknown");
            }
            catch (Exception ex)
            {
                // Log and continue — a single order failure should not abort the whole sync.
                // The job-level metrics counter will reflect the failure.
                activity?.AddException(ex);
                logger.LogError(ex, "Failed to sync order {OrderId}", order.Id);
            }
        }
    }
}
