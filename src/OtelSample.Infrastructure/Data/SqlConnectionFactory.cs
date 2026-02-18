using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OtelSample.Application.Interfaces;

namespace OtelSample.Infrastructure.Data;

/// <summary>
/// Creates and opens Microsoft.Data.SqlClient connections.
///
/// WHY Microsoft.Data.SqlClient over System.Data.SqlClient:
///   The OpenTelemetry SqlClient instrumentation package (OpenTelemetry.Instrumentation.SqlClient)
///   instruments Microsoft.Data.SqlClient. Using the legacy System.Data.SqlClient namespace
///   produces no spans. Always prefer Microsoft.Data.SqlClient for OTel compatibility.
///
/// The connection is opened here so callers receive a ready-to-use connection.
/// Dapper's query methods accept an open connection without calling Open() again.
/// </summary>
public sealed class SqlConnectionFactory(IConfiguration config) : IDbConnectionFactory
{
    private readonly string _connectionString =
        config.GetConnectionString("SqlServer")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:SqlServer is required but was not found in configuration.");

    /// <inheritdoc/>
    public async Task<IDbConnection> CreateAsync()
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        return conn;
    }
}
