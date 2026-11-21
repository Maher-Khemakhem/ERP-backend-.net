using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Tests;

/// <summary>
/// Boots the whole API in-process against a throw-away SQL Server database.
/// The database is created per fixture (so per test class), migrated by the API's own startup
/// code, and dropped afterwards. The developer database is never touched.
///
/// Server credentials come from TEST_DB_CONNECTION_STRING, or else from the same DB_* settings
/// (environment or backend-.net/SupermarketErp.Api/.env) the API uses; only the database name differs.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private const string DatabasePrefix = "supermarket_erp_test_";

    private readonly string _databaseName = DatabasePrefix + Guid.NewGuid().ToString("N");
    private readonly string _uploadDirectory = Path.Combine(Path.GetTempPath(), "erp-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _previousEnvironment = new();
    private WebApplicationFactory<Program>? _factory;
    private string _serverConnectionString = "";

    public string ConnectionString { get; private set; } = "";
    public HttpClient Client { get; private set; } = null!;
    public ErpApi Api { get; private set; } = null!;
    public WebApplicationFactory<Program> Factory => _factory!;

    public async Task InitializeAsync()
    {
        _serverConnectionString = ResolveServerConnectionString();
        var server = new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = "master" };
        ConnectionString = new SqlConnectionStringBuilder(server.ConnectionString) { InitialCatalog = _databaseName }.ConnectionString;

        try
        {
            await using var connection = new SqlConnection(server.ConnectionString);
            await connection.OpenAsync();
            await using var create = new SqlCommand($"CREATE DATABASE [{_databaseName}]", connection);
            await create.ExecuteNonQueryAsync();
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException(
                "Integration tests need a reachable SQL Server. Set TEST_DB_CONNECTION_STRING, or configure DB_HOST/DB_PORT/" +
                "DB_USERNAME/DB_PASSWORD (environment or SupermarketErp.Api/.env). The login must be allowed to CREATE DATABASE. " +
                $"Original error: {ex.Message}", ex);
        }

        // Environment variables have the highest configuration precedence, so setting them here guarantees
        // the API cannot be redirected to another database by a developer's own environment or .env file.
        Override("DB_CONNECTION_STRING", ConnectionString);
        Override("JWT_SECRET", "integration-tests-only-secret-0123456789abcdef");
        Override("APP_UPLOAD_DIR", _uploadDirectory);
        Override("ECOMMERCE_EXPIRY_SCAN_INTERVAL_MS", "300"); // let the expiry worker run quickly in tests

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        Client = _factory.CreateClient();
        Api = new ErpApi(this);

        // Host start-up applies the migrations; registering the first user doubles as a smoke test.
        var register = await Client.PostAsJsonAsync("/api/auth/register",
            new { username = "admin", displayName = "Test Admin", password = "Str0ng-Test-Passw0rd!" });
        register.EnsureSuccessStatusCode();
        var token = (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();

        foreach (var (name, value) in _previousEnvironment)
            Environment.SetEnvironmentVariable(name, value);

        SqlConnection.ClearAllPools();
        try
        {
            var master = new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = "master" };
            await using var connection = new SqlConnection(master.ConnectionString);
            await connection.OpenAsync();
            await using var drop = new SqlCommand($@"
                IF DB_ID(N'{_databaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{_databaseName}];
                END", connection);
            await drop.ExecuteNonQueryAsync();
        }
        catch (SqlException)
        {
            // Best effort: a leftover database named supermarket_erp_test_* is safe to drop manually.
        }

        try { if (Directory.Exists(_uploadDirectory)) Directory.Delete(_uploadDirectory, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Registers a storefront customer and returns a client authenticated as them.</summary>
    public async Task<(HttpClient Client, string Username)> RegisterCustomer()
    {
        var username = "cust" + Guid.NewGuid().ToString("N")[..10];
        var anonymous = _factory!.CreateClient();
        var response = await anonymous.PostAsJsonAsync("/api/store/auth/register",
            new { username, displayName = "Customer " + username, password = "Str0ng-Test-Passw0rd!" });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (anonymous, username);
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var result = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T));
    }

    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private void Override(string name, string value)
    {
        _previousEnvironment.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    public static string ResolveServerConnectionString()
    {
        var explicitConnection = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(explicitConnection)) return explicitConnection;

        LocalEnvironmentFile.Load();
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        return DatabaseConnectionString.Create(configuration);
    }
}
