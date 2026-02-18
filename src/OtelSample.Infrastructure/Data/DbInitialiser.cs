using Dapper;
using Microsoft.Extensions.Logging;
using OtelSample.Application.Interfaces;

namespace OtelSample.Infrastructure.Data;

/// <summary>
/// Creates the Orders table on startup if it does not already exist.
/// WHY: keeps the reference app self-contained — no migration tooling required.
/// In a production system, replace this with a proper migration strategy (Flyway, DbUp, etc.).
/// The OtelSample database itself is created by the db-init container in the Docker stack,
/// or must be pre-created when running the API directly on the host.
/// </summary>
public sealed class DbInitialiser(IDbConnectionFactory factory, ILogger<DbInitialiser> logger)
{
    private const string CreateTableSql = """
        IF NOT EXISTS (
            SELECT 1 FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_NAME = 'Orders'
        )
        BEGIN
            CREATE TABLE Orders (
                Id        INT IDENTITY(1,1) PRIMARY KEY,
                TenantId  NVARCHAR(100) NOT NULL,
                Status    NVARCHAR(50)  NOT NULL DEFAULT 'Pending',
                Total     DECIMAL(18,2) NOT NULL,
                CreatedAt DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
                SyncedAt  DATETIMEOFFSET NULL
            );

            -- Seed some rows so the GET endpoint returns data immediately.
            INSERT INTO Orders (TenantId, Status, Total)
            VALUES ('tenant-a', 'Pending', 99.99),
                   ('tenant-b', 'Pending', 149.50),
                   ('tenant-a', 'Completed', 59.00);
        END
        """;

    /// <summary>Ensures the database schema is ready. Call once at application startup.</summary>
    public async Task InitialiseAsync()
    {
        try
        {
            using var conn = await factory.CreateAsync();
            await conn.ExecuteAsync(CreateTableSql);
            logger.LogInformation("Database initialised successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database initialisation failed");
            throw;
        }
    }
}
