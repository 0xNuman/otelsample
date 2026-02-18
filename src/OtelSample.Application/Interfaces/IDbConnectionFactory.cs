using System.Data;

namespace OtelSample.Application.Interfaces;

/// <summary>
/// Factory abstraction for creating SQL database connections.
/// WHY: Dapper works against IDbConnection, not against a specific provider.
/// Abstracting the factory here means Infrastructure can swap Microsoft.Data.SqlClient
/// for any other ADO.NET provider without touching Application or the Facade.
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>Opens and returns a new database connection.</summary>
    Task<IDbConnection> CreateAsync();
}
