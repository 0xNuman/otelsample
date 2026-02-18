using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OpenTelemetry.Trace;
using OtelSample.Application.Context;

namespace OtelSample.Api.Middleware;

/// <summary>
/// Catches all unhandled exceptions that escape the controller/facade try-catch blocks.
///
/// WHY this exists separately from <c>RecordException</c> in the Facade:
///   Some exceptions never reach the Facade layer:
///     • Model binding failures (400) — rejected before the controller action runs.
///     • Authentication/authorisation failures (401/403) — handled by auth middleware.
///     • Middleware exceptions — thrown before the request reaches routing.
///   This handler catches all of them and ensures every 500 response has:
///     1. The exception recorded on the root HTTP span → visible in X-Ray as a "fault".
///     2. A structured log with TraceId, TenantId, and Path → queryable in CloudWatch.
///     3. A <see cref="ProblemDetails"/> response (RFC 7807) with a <c>correlationId</c>
///        field the client can include in support requests.
///
/// WHY return the trace ID as correlationId:
///   The trace ID links the client's error report directly to a specific X-Ray trace and
///   all CloudWatch logs from that request. Support engineers can jump straight to the
///   trace without any searching.
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    : IExceptionHandler
{
    /// <inheritdoc/>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext ctx, Exception ex, CancellationToken ct)
    {
        var traceId = Activity.Current?.TraceId.ToString() ?? ctx.TraceIdentifier;

        // Record on the span so X-Ray marks this trace as a fault.
        Activity.Current?.AddException(ex);
        Activity.Current?.SetStatus(ActivityStatusCode.Error, ex.Message);

        logger.LogError(ex,
            "Unhandled exception. TraceId: {TraceId}, Tenant: {TenantId}, Path: {Path}",
            traceId, TenantContext.Current, ctx.Request.Path);

        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;

        await ctx.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "An unexpected error occurred.",
            Status = StatusCodes.Status500InternalServerError,
            Extensions = { ["correlationId"] = traceId }
        }, ct);

        return true;
    }
}
