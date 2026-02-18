using System.Diagnostics.Metrics;

namespace OtelSample.Observability;

/// <summary>
/// Centralised holder for all custom application metrics.
/// WHY one class: keeps metric names, descriptions, and unit strings in a single place.
/// If a metric is renamed or removed, you change it here and the compiler finds every
/// use site. Scattered Meter.CreateCounter calls across the codebase are unmanageable.
///
/// Injected as a singleton so the Meter lifetime matches the host lifetime.
///
/// METRIC INVENTORY
/// ─────────────────────────────────────────────────────────────────────────────
/// saas.requests.active   UpDownCounter  — concurrent Facade operations in flight.
///   WHY: HTTP-level active connections are tracked by ASP.NET, but this counter is
///   scoped to the business operation (e.g. "GetOrder"). A spike here without a
///   corresponding HTTP spike means a Facade is stuck on an await (deadlock risk).
///
/// saas.job.processed.total   Counter   — background job tick outcomes.
///   Tags: job_name, status (success|failure)
///   WHY: Background jobs produce no HTTP status codes. This is the only observable
///   signal that a job silently failed. Alert threshold: any failure in a 5-minute window.
///
/// saas.external_http.duration   Histogram (seconds) — per-downstream-service latency.
///   Tags: target_service, http.status_code, tenant.id
///   WHY: OpenTelemetry's built-in http.client.request.duration aggregates ALL outbound
///   calls together. This histogram lets Grafana show latency per named dependency
///   (payment-api, email-api, etc.) so you can pinpoint which service is degraded.
///
/// saas.db.slow_query.total   Counter — queries exceeding the configured threshold.
///   Tags: query_name, tenant.id
///   WHY: SqlClient instrumentation records span duration but it is not alertable as a
///   Prometheus metric. This counter fires only above threshold (default 500 ms), giving
///   Grafana a clean signal to alert on without p99 noise from normal variation.
/// ─────────────────────────────────────────────────────────────────────────────
/// </summary>
public sealed class SaasMetrics : IDisposable
{
    /// <summary>Meter name matches the AddMeter("OtelSample.App") call in OtelConfiguration.</summary>
    public const string MeterName = "OtelSample.App";

    private readonly Meter _meter;

    /// <summary>
    /// Concurrent Facade operations in flight, tagged by operation name.
    /// Increment on entry, decrement in finally block.
    /// </summary>
    public UpDownCounter<long> ActiveRequests { get; }

    /// <summary>
    /// Count of background job ticks, tagged by job_name and status (success|failure).
    /// </summary>
    public Counter<long> JobProcessedCounter { get; }

    /// <summary>
    /// Latency histogram for outbound HTTP calls, in seconds.
    /// Tags: target_service, http.status_code, tenant.id.
    /// </summary>
    public Histogram<double> ExternalHttpDuration { get; }

    /// <summary>
    /// Count of database queries that exceeded the slow-query threshold.
    /// Tags: query_name, tenant.id.
    /// </summary>
    public Counter<long> SlowQueryCounter { get; }

    /// <summary>Initialises the Meter and all instruments.</summary>
    public SaasMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);

        ActiveRequests = _meter.CreateUpDownCounter<long>(
            name: "saas.requests.active",
            description: "Number of Facade business operations currently in flight.");

        JobProcessedCounter = _meter.CreateCounter<long>(
            name: "saas.job.processed.total",
            description: "Total background job ticks, tagged by outcome.");

        ExternalHttpDuration = _meter.CreateHistogram<double>(
            name: "saas.external_http.duration",
            unit: "s",
            description: "Latency of outbound HTTP calls per downstream service.");

        SlowQueryCounter = _meter.CreateCounter<long>(
            name: "saas.db.slow_query.total",
            description: "Database queries that exceeded the slow-query threshold.");
    }

    /// <inheritdoc/>
    public void Dispose() => _meter.Dispose();
}
