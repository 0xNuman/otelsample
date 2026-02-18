namespace OtelSample.Application.DTOs;

/// <summary>
/// Inbound request model for order creation.
/// Validated at the controller boundary; the Facade receives a clean, typed object.
/// </summary>
public sealed record CreateOrderRequest(decimal Total);
