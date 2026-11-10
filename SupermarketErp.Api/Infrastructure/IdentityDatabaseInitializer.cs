using Microsoft.Data.SqlClient;

namespace SupermarketErp.Api.Infrastructure;

public static class IdentityDatabaseInitializer
{
    // Creates the .NET-owned tables without requiring a separate migration runner.
    public static async Task EnsureCreated(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            IF SCHEMA_ID(N'platform') IS NULL EXEC(N'CREATE SCHEMA [platform]');

            IF OBJECT_ID(N'[dbo].[app_user]', N'U') IS NULL
            CREATE TABLE [dbo].[app_user] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                username VARCHAR(80) NOT NULL UNIQUE,
                display_name VARCHAR(120) NOT NULL,
                password_hash VARCHAR(255) NOT NULL,
                enabled BIT NOT NULL,
                created_at DATETIMEOFFSET NOT NULL,
                version BIGINT NOT NULL
            );

            IF OBJECT_ID(N'[dbo].[app_user_roles]', N'U') IS NULL
            CREATE TABLE [dbo].[app_user_roles] (
                user_id UNIQUEIDENTIFIER NOT NULL REFERENCES [dbo].[app_user](id),
                role VARCHAR(40) NOT NULL,
                PRIMARY KEY (user_id, role),
                CONSTRAINT app_user_roles_role_check CHECK (role IN (
                    'ADMIN', 'MANAGER', 'CASHIER', 'INVENTORY_MANAGER', 'ACCOUNTANT', 'CUSTOMER'
                ))
            );

            IF OBJECT_ID(N'[platform].[dotnet_outbox]', N'U') IS NULL
            CREATE TABLE [platform].[dotnet_outbox] (
                id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                event_type VARCHAR(160) NOT NULL,
                payload NVARCHAR(MAX) NOT NULL,
                occurred_at DATETIMEOFFSET NOT NULL,
                processed_at DATETIMEOFFSET NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                last_error NVARCHAR(MAX) NULL
            );

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE name = N'ix_dotnet_outbox_pending'
                  AND object_id = OBJECT_ID(N'[platform].[dotnet_outbox]')
            )
            CREATE INDEX ix_dotnet_outbox_pending
                ON [platform].[dotnet_outbox](occurred_at)
                WHERE processed_at IS NULL;
            """, connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}
