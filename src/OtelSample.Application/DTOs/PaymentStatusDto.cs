namespace OtelSample.Application.DTOs;

/// <summary>
/// Response model from the downstream payment service.
/// WHY a separate DTO: isolates the app from payment API contract changes.
/// </summary>
public sealed record PaymentStatusDto(string Status, string? FailureReason);
