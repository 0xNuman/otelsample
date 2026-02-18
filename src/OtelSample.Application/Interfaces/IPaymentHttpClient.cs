using OtelSample.Application.DTOs;

namespace OtelSample.Application.Interfaces;

/// <summary>
/// Abstraction for the downstream payment service HTTP client.
/// WHY define the interface in Application: the Facade layer can depend on it without
/// pulling in any HttpClient or infrastructure concern. Infrastructure provides the
/// concrete implementation; tests can substitute a fake.
/// </summary>
public interface IPaymentHttpClient
{
    /// <summary>Fetches the payment status for the given order from the payment service.</summary>
    Task<PaymentStatusDto?> GetPaymentStatusAsync(int orderId, CancellationToken ct = default);
}
