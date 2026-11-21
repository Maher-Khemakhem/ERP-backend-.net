using Microsoft.Data.SqlClient;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Tests;

/// <summary>
/// Upgrading an existing installation: migration 0002 must clean up duplicate open low-stock alerts
/// (created by the old, non-idempotent handler) before it can enforce one open alert per product.
/// </summary>
public sealed class MigrationTests
{
    [Fact]
    public async Task Upgrading_an_existing_database_cleans_duplicates_and_adds_the_new_constraints()
    {
        var database = "supermarket_erp_test_" + Guid.NewGuid().ToString("N");
        var server = new SqlConnectionStringBuilder(ApiFixture.ResolveServerConnectionString()) { InitialCatalog = "master" };
        var scratch = new SqlConnectionStringBuilder(server.ConnectionString) { InitialCatalog = database };
        await Run(server.ConnectionString, $"CREATE DATABASE [{database}]");
        try
        {
            // An installation as it was before 0002: schema from 0001 only, no migration history recorded.
            await Run(scratch.ConnectionString, await ReadMigration("0001_initial.sql"));
            var (crowded, quiet) = (Guid.NewGuid(), Guid.NewGuid());
            var oldest = Guid.NewGuid();
            await Run(scratch.ConnectionString, $@"
                INSERT INTO procurement.low_stock_alert(id, product_id, raised_at, acknowledged) VALUES
                  ('{oldest}',        '{crowded}', '2026-01-01T10:00:00+00:00', 0),
                  ('{Guid.NewGuid()}','{crowded}', '2026-01-02T10:00:00+00:00', 0),
                  ('{Guid.NewGuid()}','{crowded}', '2026-01-03T10:00:00+00:00', 0),
                  ('{Guid.NewGuid()}','{crowded}', '2026-01-04T10:00:00+00:00', 1),
                  ('{Guid.NewGuid()}','{quiet}',   '2026-01-05T10:00:00+00:00', 0)");

            var applied = await DatabaseMigrator.ApplyPendingAsync(new SqlServerDataSource(scratch.ConnectionString));

            Assert.Contains(applied, name => name.StartsWith("0002_"));
            Assert.Contains(applied, name => name.StartsWith("0003_"));
            Assert.Equal(5, await Scalar<int>(scratch, "SELECT COUNT(*) FROM procurement.low_stock_alert")); // nothing deleted
            Assert.Equal(oldest, await Scalar<Guid>(scratch,
                $"SELECT id FROM procurement.low_stock_alert WHERE product_id = '{crowded}' AND acknowledged = 0"));
            Assert.Equal(1, await Scalar<int>(scratch,
                $"SELECT COUNT(*) FROM procurement.low_stock_alert WHERE product_id = '{quiet}' AND acknowledged = 0"));
            Assert.Equal(1, await Scalar<int>(scratch, "SELECT COUNT(*) FROM sys.indexes WHERE name = 'ux_low_stock_alert_open_product'"));
            Assert.Equal(2, await Scalar<int>(scratch,
                "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('platform.dotnet_outbox') AND name IN ('failed_at','next_attempt_at')"));

            // 0003: nullable UNIQUE constraints are replaced by NULL-tolerant unique indexes
            Assert.Equal(0, await Scalar<int>(scratch, @"
                SELECT COUNT(*) FROM sys.key_constraints kc
                JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE kc.type = 'UQ' AND c.name IN ('payment_reference', 'checkout_session_id')
                  AND kc.parent_object_id IN (OBJECT_ID('ecommerce.online_order'), OBJECT_ID('ecommerce.stripe_checkout'))"));
            Assert.Equal(2, await Scalar<int>(scratch,
                "SELECT COUNT(*) FROM sys.indexes WHERE is_unique = 1 AND has_filter = 1 AND name IN ('ux_online_order_payment_reference', 'ux_stripe_checkout_session_id')"));

            // the constraint now holds
            var duplicate = await Assert.ThrowsAsync<SqlException>(() => Run(scratch.ConnectionString,
                $"INSERT INTO procurement.low_stock_alert(id, product_id, raised_at, acknowledged) VALUES (NEWID(), '{quiet}', SYSDATETIMEOFFSET(), 0)"));
            Assert.Equal(2601, duplicate.Number);

            // and running the migrator again is a no-op
            Assert.Empty(await DatabaseMigrator.ApplyPendingAsync(new SqlServerDataSource(scratch.ConnectionString)));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await Run(server.ConnectionString, $@"
                IF DB_ID(N'{database}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{database}];
                END");
        }
    }

    private static async Task<string> ReadMigration(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "SupermarketErp.Api", "Migrations", fileName);
            if (File.Exists(path)) return await File.ReadAllTextAsync(path);
        }
        throw new FileNotFoundException($"Could not find migration {fileName} above {AppContext.BaseDirectory}.");
    }

    private static async Task Run(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(SqlConnectionStringBuilder database, string sql)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
