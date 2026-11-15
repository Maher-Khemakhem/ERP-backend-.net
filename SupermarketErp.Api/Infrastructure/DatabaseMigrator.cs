using System.Reflection;
using Microsoft.Data.SqlClient;

namespace SupermarketErp.Api.Infrastructure;

/// <summary>Applies ordered, embedded SQL migrations and records each applied version.</summary>
public static class DatabaseMigrator
{
    private const string ResourcePrefix = "SupermarketErp.Api.Migrations.";

    public static async Task<IReadOnlyList<string>> ApplyPendingAsync(
        SqlServerDataSource dataSource,
        CancellationToken ct = default)
    {
        var migrations = DiscoverMigrations();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        await using (var lockCommand = new SqlCommand("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = N'supermarket-erp-schema-migrations',
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction',
                @LockTimeout = 60000;
            SELECT @result;
            """, connection, transaction))
        {
            var result = Convert.ToInt32(await lockCommand.ExecuteScalarAsync(ct));
            if (result < 0)
                throw new InvalidOperationException($"Could not acquire the database migration lock ({result}).");
        }

        await using (var historyCommand = new SqlCommand("""
            IF SCHEMA_ID(N'platform') IS NULL EXEC(N'CREATE SCHEMA [platform]');
            IF OBJECT_ID(N'[platform].[schema_migrations]', N'U') IS NULL
            BEGIN
                CREATE TABLE [platform].[schema_migrations](
                    [version] VARCHAR(32) NOT NULL PRIMARY KEY,
                    [name] VARCHAR(200) NOT NULL,
                    [applied_at] DATETIMEOFFSET NOT NULL
                );
            END;
            """, connection, transaction))
        {
            await historyCommand.ExecuteNonQueryAsync(ct);
        }

        var applied = new HashSet<string>(StringComparer.Ordinal);
        await using (var readHistory = new SqlCommand(
            "SELECT [version] FROM [platform].[schema_migrations];", connection, transaction))
        await using (var reader = await readHistory.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                applied.Add(reader.GetString(0));
        }

        var newlyApplied = new List<string>();
        foreach (var migration in migrations)
        {
            if (applied.Contains(migration.Version))
                continue;

            await using (var command = new SqlCommand(migration.Sql, connection, transaction))
                await command.ExecuteNonQueryAsync(ct);

            await using (var record = new SqlCommand("""
                INSERT INTO [platform].[schema_migrations]([version], [name], [applied_at])
                VALUES(@version, @name, @appliedAt);
                """, connection, transaction))
            {
                record.Parameters.AddWithValue("version", migration.Version);
                record.Parameters.AddWithValue("name", migration.Name);
                record.Parameters.AddWithValue("appliedAt", DateTimeOffset.UtcNow);
                await record.ExecuteNonQueryAsync(ct);
            }

            newlyApplied.Add($"{migration.Version}_{migration.Name}");
        }

        await transaction.CommitAsync(ct);
        return newlyApplied;
    }

    private static IReadOnlyList<Migration> DiscoverMigrations()
    {
        var assembly = typeof(DatabaseMigrator).Assembly;
        var migrations = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(name => ReadMigration(assembly, name))
            .OrderBy(migration => migration.Version, StringComparer.Ordinal)
            .ToArray();

        if (migrations.Length == 0)
            throw new InvalidOperationException("No embedded SQL migrations were found.");
        if (migrations.Select(migration => migration.Version).Distinct(StringComparer.Ordinal).Count() != migrations.Length)
            throw new InvalidOperationException("SQL migration versions must be unique.");

        return migrations;
    }

    private static Migration ReadMigration(Assembly assembly, string resourceName)
    {
        var filename = resourceName[ResourcePrefix.Length..^4];
        var separator = filename.IndexOf('_');
        if (separator <= 0 || separator == filename.Length - 1)
            throw new InvalidOperationException($"Migration resource '{resourceName}' must be named NNNN_name.sql.");

        var version = filename[..separator];
        if (version.Length != 4 || !version.All(char.IsAsciiDigit))
            throw new InvalidOperationException($"Migration resource '{resourceName}' must use a four-digit version.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Could not open migration resource '{resourceName}'.");
        using var reader = new StreamReader(stream);
        return new Migration(version, filename[(separator + 1)..], reader.ReadToEnd());
    }

    private sealed record Migration(string Version, string Name, string Sql);
}
