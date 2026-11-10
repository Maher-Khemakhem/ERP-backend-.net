using Microsoft.Data.SqlClient;

namespace SupermarketErp.Api.Infrastructure;

public static class DatabaseConnectionString
{
    public static string Create(IConfiguration configuration)
    {
        var supplied = configuration["DB_CONNECTION_STRING"];
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            return supplied;
        }

        var jdbcUrl = configuration["DB_URL"];
        if (!string.IsNullOrWhiteSpace(jdbcUrl))
        {
            const string jdbcPrefix = "jdbc:sqlserver://";
            var sqlServerUrl = jdbcUrl.StartsWith(jdbcPrefix, StringComparison.OrdinalIgnoreCase)
                ? jdbcUrl[jdbcPrefix.Length..]
                : jdbcUrl;
            if (Uri.TryCreate("sqlserver://" + sqlServerUrl, UriKind.Absolute, out var uri)
                && uri.Scheme.Equals("sqlserver", StringComparison.OrdinalIgnoreCase))
            {
                var builder = new SqlConnectionStringBuilder
                {
                    DataSource = uri.IsDefaultPort ? uri.Host : $"{uri.Host},{uri.Port}",
                    InitialCatalog = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
                    UserID = configuration["DB_USERNAME"] ?? "sa",
                    Password = configuration["DB_PASSWORD"] ?? "replace-with-your-sql-server-password",
                    TrustServerCertificate = true
                };
                return builder.ConnectionString;
            }

            return jdbcUrl;
        }

        return new SqlConnectionStringBuilder
        {
            DataSource = BuildDataSource(configuration),
            InitialCatalog = configuration["DB_NAME"] ?? "supermarket_erp_saas_dev",
            UserID = configuration["DB_USERNAME"] ?? "sa",
            Password = configuration["DB_PASSWORD"] ?? "replace-with-your-sql-server-password",
            TrustServerCertificate = true
        }.ConnectionString;
    }

    private static string BuildDataSource(IConfiguration configuration)
    {
        var host = configuration["DB_HOST"] ?? "localhost";
        return int.TryParse(configuration["DB_PORT"], out var port) && port > 0
            ? $"{host},{port}"
            : host;
    }
}
