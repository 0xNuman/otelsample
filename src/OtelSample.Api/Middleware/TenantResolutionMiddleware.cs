using System.Diagnostics;
using OtelSample.Application.Context;
using Serilog.Context;

namespace OtelSample.Api.Middleware;

/// <summary>
/// Reads the <c>X-Tenant-Id</c> request header and propagates the tenant identifier
/// through three channels simultaneously:
///
///   1. <see cref="TenantContext.Current"/> — AsyncLocal ambient value so every
///      downstream method can read the tenant ID without it being passed as a parameter.
///
///   2. <see cref="Activity.Current"/> tag — tags the active OTel span with tenant.id.
///      Because OTel propagates tags to child spans, every DB and HTTP span created
///      within this request automatically inherits the tag. You never need to set it again.
///
///   3. Serilog <see cref="LogContext"/> — pushes TenantId as a structured property
///      so every log event emitted during this request carries TenantId as a queryable
///      field in CloudWatch Insights.
///
/// WHY one middleware handles all three:
///   Consistency — if the header is missing, all three channels get "unknown" rather
///   than a mix of null, empty string, and absent property. A future change to the
///   fallback value updates one place.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    /// <summary>Invokes the middleware, setting tenant context before calling the next handler.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var tenantId = context.Request.Headers["X-Tenant-Id"].FirstOrDefault() ?? "unknown";

        // 1. AsyncLocal — flows through all awaits in this request's call tree.
        TenantContext.Current = tenantId;

        // 2. OTel span tag — inherited by all child spans automatically.
        Activity.Current?.SetTag("tenant.id", tenantId);

        // 3. Serilog structured property — present on every log line in this request.
        using (LogContext.PushProperty("TenantId", tenantId))
        {
            await next(context);
        }
    }
}
