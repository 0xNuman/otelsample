namespace OtelSample.Application.DTOs;

/// <summary>
/// Data transfer object for an order. Used across the Controller → Facade → Repository boundary.
/// Keeping DTOs in Application ensures no layer above Infrastructure leaks persistence types.
/// </summary>
public sealed record OrderDto(
    int Id,
    string TenantId,
    string Status,
    decimal Total,
    DateTimeOffset CreatedAt);
