using Microsoft.Data.SqlClient;

namespace SupermarketErp.Api.Infrastructure;

public sealed class SqlServerDataSource(string connectionString)
{
    public string ConnectionString => connectionString;

    public async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public SqlConnection CreateConnection() => new(connectionString);
}
