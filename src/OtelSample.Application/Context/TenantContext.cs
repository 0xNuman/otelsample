namespace OtelSample.Application.Context;

/// <summary>
/// Ambient tenant context propagated via AsyncLocal.
/// Every OTel span, metric, and log must carry tenant.id as a tag/attribute.
/// Without this, you cannot filter failures or latency spikes per tenant in Grafana or X-Ray.
///
/// WHY AsyncLocal: It flows automatically through async continuations (await), so a value
/// set at the start of a request is visible in every downstream awaited call — repositories,
/// HTTP clients, background tasks spawned within the same logical flow — without any
/// explicit parameter passing.
/// </summary>
public static class TenantContext
{
    private static readonly AsyncLocal<string?> _tenantId = new();

    /// <summary>
    /// Gets or sets the current tenant identifier for this async execution context.
    /// </summary>
    public static string? Current
    {
        get => _tenantId.Value;
        set => _tenantId.Value = value;
    }
}
