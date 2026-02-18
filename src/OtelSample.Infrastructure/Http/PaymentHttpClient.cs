using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using OtelSample.Application.Context;
using OtelSample.Application.DTOs;
using OtelSample.Application.Interfaces;
using OtelSample.Observability;

namespace OtelSample.Infrastructure.Http;

/// <summary>
/// Typed HttpClient wrapper for the downstream payment service.
///
/// OpenTelemetry.Instrumentation.Http auto-creates a span for every HttpClient call,
/// capturing: http.method, url.full, http.response.status_code, error.type.
///
/// This wrapper adds two things that auto-instrumentation cannot provide:
///   1. saas.external_http.duration histogram — breaks down latency PER downstream
///      service by name (target_service tag). The built-in http.client.request.duration
///      aggregates all outbound calls, so you cannot tell which dependency is slow.
///   2. Business-context logging — the exception log includes orderId and tenantId,
///      making CloudWatch Insights queries possible:
///      "find all payment failures for tenant X in the last hour".
///
/// WHY a typed HttpClient over injecting IHttpClientFactory directly:
///   The typed client pattern guarantees that the target_service tag ("payment-api")
///   is always set by this class. With raw IHttpClientFactory, callers routinely forget
///   to tag, producing ungrouped metric data.
/// </summary>
public sealed class PaymentHttpClient(
    HttpClient httpClient,
    SaasMetrics metrics,
    ILogger<PaymentHttpClient> logger) : IPaymentHttpClient
{
    /// <inheritdoc/>
    public async Task<PaymentStatusDto?> GetPaymentStatusAsync(int orderId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var statusCode = "unknown";

        try
        {
            var response = await httpClient.GetAsync($"/payments/{orderId}", ct);
            statusCode = ((int)response.StatusCode).ToString();

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Payment service returned {StatusCode} for order {OrderId}, tenant {TenantId}",
                    statusCode, orderId, TenantContext.Current);

                // Return null rather than throwing — the caller decides whether a missing
                // payment status is a hard failure or a degraded-mode operation.
                return null;
            }

            return await response.Content.ReadFromJsonAsync<PaymentStatusDto>(ct);
        }
        catch (HttpRequestException ex)
        {
            // WHY log here despite the HttpClient span also recording the exception:
            //   The span records the exception type and message. This log adds business
            //   context (orderId, tenantId) that is queryable in CloudWatch Insights.
            logger.LogError(ex,
                "Payment service call failed for order {OrderId}, tenant {TenantId}, last known status {StatusCode}",
                orderId, TenantContext.Current, statusCode);
            throw;
        }
        finally
        {
            sw.Stop();

            // Record duration regardless of success/failure so both outcomes appear
            // in the histogram. Grafana can then show p99 for 200 vs 5xx separately.
            metrics.ExternalHttpDuration.Record(
                sw.Elapsed.TotalSeconds,
                new TagList
                {
                    { "target_service", "payment-api" },
                    { "http.status_code", statusCode },
                    { "tenant.id", TenantContext.Current ?? "unknown" }
                });
        }
    }
}
