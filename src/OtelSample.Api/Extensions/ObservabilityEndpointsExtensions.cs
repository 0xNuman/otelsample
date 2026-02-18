using OpenTelemetry.Metrics;

namespace OtelSample.Api.Extensions;

/// <summary>
/// Extension methods to map observability endpoints on the <see cref="WebApplication"/>.
/// Kept in the Api project (not in Observability) because <see cref="WebApplication"/>
/// is an ASP.NET Core type that the class library Observability project does not reference.
/// </summary>
public static class ObservabilityEndpointsExtensions
{
    /// <summary>
    /// Maps the Prometheus <c>/metrics</c> scrape endpoint in local mode.
    /// In AWS mode metrics are pushed via OTLP to the ADOT Collector, so no scrape
    /// endpoint is needed and this method is a no-op.
    /// </summary>
    public static WebApplication MapObservabilityEndpoints(this WebApplication app)
    {
        var isAwsMode = string.Equals(
            app.Configuration["Otel:Mode"], "aws", StringComparison.OrdinalIgnoreCase);

        if (!isAwsMode)
        {
            // WHY /metrics: Prometheus convention. The prometheus.yml scrape config
            // targets this path. Grafana queries Prometheus, which queries this endpoint.
            app.MapPrometheusScrapingEndpoint("/metrics");
        }

        return app;
    }
}
