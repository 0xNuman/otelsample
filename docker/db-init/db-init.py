"""
One-shot database initialiser for the OtelSample local dev stack.

Runs after the sqlserver container passes its healthcheck, creates the
OtelSample database and Orders table (both idempotent), then exits.
The api service depends on this container completing successfully.
"""

import os
import sys
import time

import pymssql

HOST = os.environ["DB_HOST"]
PASSWORD = os.environ["DB_PASSWORD"]


def connect(database: str):
    return pymssql.connect(
        server=HOST,
        user="sa",
        password=PASSWORD,
        database=database,
        login_timeout=10,
    )


def ensure_database():
    conn = connect("master")
    # CREATE DATABASE cannot run inside a transaction; autocommit disables the
    # implicit transaction wrapper that pymssql applies by default.
    conn.autocommit(True)
    cursor = conn.cursor()
    cursor.execute(
        "IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = 'OtelSample') "
        "CREATE DATABASE OtelSample;"
    )
    conn.close()
    print("Database OtelSample ensured.")


def ensure_schema():
    # Retry briefly — the freshly-created database may take a moment to come online.
    for attempt in range(1, 7):
        try:
            conn = connect("OtelSample")
            break
        except pymssql.OperationalError as exc:
            if attempt == 6:
                raise
            print(f"  OtelSample not yet accessible (attempt {attempt}/6): {exc}")
            time.sleep(attempt * 2)

    cursor = conn.cursor()
    cursor.execute("""
        IF NOT EXISTS (
            SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Orders'
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

            INSERT INTO Orders (TenantId, Status, Total)
            VALUES ('tenant-a', 'Pending', 99.99),
                   ('tenant-b', 'Pending', 149.50),
                   ('tenant-a', 'Completed', 59.00);
        END
    """)
    conn.commit()
    conn.close()
    print("Schema initialised successfully.")


if __name__ == "__main__":
    print(f"Connecting to SQL Server at {HOST}...")
    try:
        ensure_database()
        ensure_schema()
    except Exception as exc:
        print(f"Initialisation failed: {exc}", file=sys.stderr)
        sys.exit(1)
