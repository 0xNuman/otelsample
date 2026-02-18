using OtelSample.Application.DTOs;

namespace OtelSample.Application.Interfaces;

/// <summary>
/// Repository abstraction for order persistence.
/// Defined in Application so the Facade depends only on this interface, not on
/// any Infrastructure type. This is the Dependency Inversion Principle in practice.
/// </summary>
public interface IOrderRepository
{
    /// <summary>Gets a single order by its identifier, scoped to the current tenant.</summary>
    Task<OrderDto?> GetByIdAsync(int id, CancellationToken ct = default);

    /// <summary>Persists a new order and returns its generated identifier.</summary>
    Task<int> CreateAsync(string tenantId, decimal total, CancellationToken ct = default);

    /// <summary>Returns all orders in "Pending" status for sync processing.</summary>
    Task<IReadOnlyList<OrderDto>> GetPendingAsync(CancellationToken ct = default);

    /// <summary>Marks an order as synced.</summary>
    Task MarkSyncedAsync(int id, CancellationToken ct = default);
}
