using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OtelSample.Application.Context;
using OtelSample.Application.DTOs;
using OtelSample.Application.Interfaces;
using OtelSample.Observability;

namespace OtelSample.Infrastructure.Data;

/// <summary>
/// Dapper repository for the Orders table.
///
/// OpenTelemetry.Instrumentation.SqlClient automatically creates a child span for every
/// SQL command executed through Microsoft.Data.SqlClient, capturing:
///   • db.statement  — the SQL text (when SetDbStatementForText = true)
///   • db.system     — "mssql"
///   • db.name       — the database name
///   • net.peer.name — the server hostname
///
/// This repository layer adds slow-query detection on top of the auto-instrumentation.
///
/// WHY slow-query detection here and not in the Facade:
///   The Facade measures end-to-end operation time (including business logic).
///   The repository measures pure database roundtrip time.
///   A slow DB query might not make the Facade slow if it is cached or retried;
///   monitoring at the DB call site gives a more accurate signal.
///
/// WHY a counter rather than relying on histogram p99:
///   saas.db.slow_query.total fires only when the threshold is crossed, making it
///   suitable for a zero-tolerance alert ("any slow query in 5 minutes → page").
///   p99 alerts require sustained degradation to trigger and produce more false negatives.
/// </summary>
public sealed class OrderRepository(
    IDbConnectionFactory connectionFactory,
    SaasMetrics metrics,
    IConfiguration config,
    ILogger<OrderRepository> logger) : IOrderRepository
{
    private int SlowQueryThresholdMs =>
        config.GetValue("Otel:SlowQueryThresholdMs", 500);

    /// <inheritdoc/>
    public async Task<OrderDto?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        const string queryName = "Orders.GetById";
        var sw = Stopwatch.StartNew();

        using var conn = await connectionFactory.CreateAsync();

        // Dapper maps the result set columns to OrderDto constructor parameters by name.
        var result = await conn.QuerySingleOrDefaultAsync<OrderDto>(
            new CommandDefinition(
                commandText: "SELECT Id, TenantId, Status, Total, CreatedAt FROM Orders WHERE Id = @id AND TenantId = @tenantId",
                parameters: new { id, tenantId = TenantContext.Current ?? "unknown" },
                cancellationToken: ct));

        sw.Stop();
        RecordSlowQuery(queryName, sw.ElapsedMilliseconds);
        return result;
    }

    /// <inheritdoc/>
    public async Task<int> CreateAsync(string tenantId, decimal total, CancellationToken ct = default)
    {
        const string queryName = "Orders.Create";
        var sw = Stopwatch.StartNew();

        using var conn = await connectionFactory.CreateAsync();

        var id = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
                commandText: """
                    INSERT INTO Orders (TenantId, Status, Total)
                    OUTPUT INSERTED.Id
                    VALUES (@tenantId, 'Pending', @total)
                    """,
                parameters: new { tenantId, total },
                cancellationToken: ct));

        sw.Stop();
        RecordSlowQuery(queryName, sw.ElapsedMilliseconds);
        return id;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OrderDto>> GetPendingAsync(CancellationToken ct = default)
    {
        const string queryName = "Orders.GetPending";
        var sw = Stopwatch.StartNew();

        using var conn = await connectionFactory.CreateAsync();

        var results = await conn.QueryAsync<OrderDto>(
            new CommandDefinition(
                commandText: "SELECT Id, TenantId, Status, Total, CreatedAt FROM Orders WHERE Status = 'Pending'",
                cancellationToken: ct));

        sw.Stop();
        RecordSlowQuery(queryName, sw.ElapsedMilliseconds);
        return [.. results];
    }

    /// <inheritdoc/>
    public async Task MarkSyncedAsync(int id, CancellationToken ct = default)
    {
        const string queryName = "Orders.MarkSynced";
        var sw = Stopwatch.StartNew();

        using var conn = await connectionFactory.CreateAsync();

        await conn.ExecuteAsync(
            new CommandDefinition(
                commandText: "UPDATE Orders SET Status = 'Synced', SyncedAt = SYSDATETIMEOFFSET() WHERE Id = @id",
                parameters: new { id },
                cancellationToken: ct));

        sw.Stop();
        RecordSlowQuery(queryName, sw.ElapsedMilliseconds);
    }

    private void RecordSlowQuery(string queryName, long elapsedMs)
    {
        if (elapsedMs <= SlowQueryThresholdMs) return;

        metrics.SlowQueryCounter.Add(1, new TagList
        {
            { "query_name", queryName },
            { "tenant.id", TenantContext.Current ?? "unknown" }
        });

        logger.LogWarning(
            "Slow query detected: {QueryName} took {ElapsedMs}ms for tenant {TenantId} (threshold: {ThresholdMs}ms)",
            queryName, elapsedMs, TenantContext.Current, SlowQueryThresholdMs);
    }
}
