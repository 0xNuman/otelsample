using OtelSample.Application.DTOs;

namespace OtelSample.Application.Interfaces;

/// <summary>
/// Facade interface for order use cases.
/// WHY a Facade interface: the Controller only knows about this contract, not about
/// concrete repository or HTTP client implementations. This also makes unit testing
/// the controller trivially easy — just mock this interface.
/// </summary>
public interface IOrderFacade
{
    /// <summary>Retrieves an order, enriching the response with payment status.</summary>
    Task<OrderDto?> GetOrderAsync(int id, CancellationToken ct = default);

    /// <summary>Creates a new order for the current tenant.</summary>
    Task<int> CreateOrderAsync(CreateOrderRequest request, CancellationToken ct = default);

    /// <summary>Synchronises all pending orders — called by the background job.</summary>
    Task SyncPendingOrdersAsync(CancellationToken ct = default);
}
