CREATE DATABASE [OtelSample]
GO

USE [OtelSample];
GO

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Orders')
BEGIN
    CREATE TABLE Orders (
        Id        INT IDENTITY(1,1) PRIMARY KEY,
        TenantId  NVARCHAR(100) NOT NULL,
        Status    NVARCHAR(50)  NOT NULL DEFAULT 'Pending',
        Total     DECIMAL(18,2) NOT NULL,
        CreatedAt DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
        SyncedAt  DATETIMEOFFSET NULL
    );

    INSERT INTO Orders (TenantId, Status, Total)
    VALUES ('tenant-a', 'Pending', 99.99),
            ('tenant-b', 'Pending', 149.50),
            ('tenant-a', 'Completed', 59.00);
END
