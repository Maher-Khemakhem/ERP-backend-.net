using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Data.SqlClient;

namespace SupermarketErp.Api.Features.Auth;

public sealed class AuthService(
    NpgsqlDataSource dataSource,
    IConfiguration configuration)
{
    private readonly string _issuer = configuration["JWT_ISSUER"] ?? "supermarket-erp";
    private readonly string _audience = configuration["JWT_AUDIENCE"] ?? "supermarket-web";
    private readonly byte[] _secret = Encoding.UTF8.GetBytes(configuration["JWT_SECRET"]!);
    private readonly int _expirationSeconds = int.TryParse(configuration["JWT_EXPIRATION_SECONDS"], out var seconds)
        ? seconds
        : 900;

    public async Task<bool> SetupRequired(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT NOT EXISTS (SELECT 1 FROM dbo.app_user)", connection);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? true);
    }

    public async Task<AuthResponse> RegisterFirstAdmin(RegisterRequest request, CancellationToken ct)
    {
        ValidateCredentials(request.Username, request.DisplayName, request.Password);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        await using (var lockCommand = new NpgsqlCommand(
            "EXEC sp_getapplock @Resource = 'supermarket-erp-first-admin', @LockMode = 'Exclusive', @LockOwner = 'Transaction'",
            connection, transaction))
        {
            await lockCommand.ExecuteNonQueryAsync(ct);
        }

        await using (var countCommand = new NpgsqlCommand("SELECT COUNT(*) FROM dbo.app_user", connection, transaction))
        {
            if ((long)(await countCommand.ExecuteScalarAsync(ct) ?? 0L) != 0)
            {
                throw new InvalidOperationException("The initial administrator has already been created.");
            }
        }

        var user = await InsertUser(connection, transaction, request, [Roles.Admin], ct);
        await transaction.CommitAsync(ct);
        return CreateAuthResponse(user);
    }

    public async Task<AuthResponse> RegisterCustomer(RegisterRequest request, CancellationToken ct)
    {
        ValidateCredentials(request.Username, request.DisplayName, request.Password);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        var user = await InsertUser(connection, transaction, request, [Roles.Customer], ct);
        await transaction.CommitAsync(ct);
        return CreateAuthResponse(user);
    }

    public async Task<AuthResponse> Login(LoginRequest request, bool customerOnly, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT u.id, u.username, u.display_name, u.password_hash, u.enabled, u.created_at,
                   COALESCE(STRING_AGG(r.role, ','), '')
            FROM dbo.app_user u
            LEFT JOIN dbo.app_user_roles r ON r.user_id = u.id
            WHERE lower(u.username) = lower(@username)
            GROUP BY u.id
            """, connection);
        command.Parameters.AddWithValue("username", request.Username.Trim());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidCredentialsException();
        }

        var roles = reader.GetString(6).Split(',', StringSplitOptions.RemoveEmptyEntries);
        var user = new UserRecord(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetBoolean(4), ReadCreatedAt(reader, 5), roles);
        if (!user.Enabled || (customerOnly && !user.Roles.Contains(Roles.Customer))
            || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            throw new InvalidCredentialsException();
        }

        return CreateAuthResponse(user);
    }

    public async Task<UserView?> FindByUsername(string username, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT u.id, u.username, u.display_name, u.password_hash, u.enabled, u.created_at,
                   COALESCE(STRING_AGG(r.role, ','), '')
            FROM dbo.app_user u
            LEFT JOIN dbo.app_user_roles r ON r.user_id = u.id
            WHERE lower(u.username) = lower(@username)
            GROUP BY u.id
            """, connection);
        command.Parameters.AddWithValue("username", username);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadUser(reader).ToView() : null;
    }

    public async Task<IReadOnlyList<UserView>> ListUsers(CancellationToken ct)
    {
        var users = new List<UserView>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT u.id, u.username, u.display_name, u.password_hash, u.enabled, u.created_at,
                   COALESCE(STRING_AGG(r.role, ','), '')
            FROM dbo.app_user u
            LEFT JOIN dbo.app_user_roles r ON r.user_id = u.id
            GROUP BY u.id
            ORDER BY u.username
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) users.Add(ReadUser(reader).ToView());
        return users;
    }

    public async Task<UserView> CreateManagedUser(CreateUserRequest request, CancellationToken ct)
    {
        ValidateCredentials(request.Username, request.DisplayName, request.Password);
        var roles = ValidateRoles(request.Roles);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        var user = await InsertUser(connection, transaction,
            new RegisterRequest(request.Username, request.DisplayName, request.Password), roles, ct);
        await transaction.CommitAsync(ct);
        return user.ToView();
    }

    public async Task<UserView> UpdateRoles(Guid userId, HashSet<string> rolesInput, CancellationToken ct)
    {
        var roles = ValidateRoles(rolesInput);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        await using (var delete = new NpgsqlCommand("DELETE FROM dbo.app_user_roles WHERE user_id = @id", connection, transaction))
        {
            delete.Parameters.AddWithValue("id", userId);
            await delete.ExecuteNonQueryAsync(ct);
        }
        await using (var check = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM dbo.app_user WHERE id = @id)", connection, transaction))
        {
            check.Parameters.AddWithValue("id", userId);
            if (!(bool)(await check.ExecuteScalarAsync(ct) ?? false))
                throw new ArgumentException($"User not found: {userId}");
        }
        await using (var updateVersion = new NpgsqlCommand(
            "UPDATE dbo.app_user SET version = version + 1 WHERE id = @id", connection, transaction))
        {
            updateVersion.Parameters.AddWithValue("id", userId);
            await updateVersion.ExecuteNonQueryAsync(ct);
        }
        foreach (var role in roles)
        {
            await using var insertRole = new NpgsqlCommand(
                "INSERT INTO dbo.app_user_roles(user_id, role) VALUES(@id, @role)", connection, transaction);
            insertRole.Parameters.AddWithValue("id", userId);
            insertRole.Parameters.AddWithValue("role", role);
            await insertRole.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return await FindUserById(userId, ct) ?? throw new ArgumentException($"User not found: {userId}");
    }

    public async Task<UserView> UpdateEnabled(Guid userId, bool enabled, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "UPDATE dbo.app_user SET enabled = @enabled, version = version + 1 WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("enabled", enabled);
        if (await command.ExecuteNonQueryAsync(ct) == 0) throw new ArgumentException($"User not found: {userId}");
        return await FindUserById(userId, ct) ?? throw new ArgumentException($"User not found: {userId}");
    }

    private async Task<UserView?> FindUserById(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT u.id, u.username, u.display_name, u.password_hash, u.enabled, u.created_at,
                   COALESCE(STRING_AGG(r.role, ','), '')
            FROM dbo.app_user u
            LEFT JOIN dbo.app_user_roles r ON r.user_id = u.id
            WHERE u.id = @id GROUP BY u.id
            """, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadUser(reader).ToView() : null;
    }

    private async Task<UserRecord> InsertUser(NpgsqlConnection connection, NpgsqlTransaction transaction,
        RegisterRequest request, IReadOnlyCollection<string> roles, CancellationToken ct)
    {
        var username = request.Username.Trim().ToLowerInvariant();
        var id = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO dbo.app_user(id, username, display_name, password_hash, enabled, created_at, version)
            VALUES(@id, @username, @display_name, @password_hash, 1, @created_at, 0)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("username", username);
            insert.Parameters.AddWithValue("display_name", request.DisplayName.Trim());
            insert.Parameters.AddWithValue("password_hash", BCrypt.Net.BCrypt.HashPassword(request.Password));
            insert.Parameters.AddWithValue("created_at", createdAt);
            await insert.ExecuteNonQueryAsync(ct);
        }
        foreach (var role in roles)
        {
            await using var insertRole = new NpgsqlCommand(
                "INSERT INTO dbo.app_user_roles(user_id, role) VALUES(@id, @role)", connection, transaction);
            insertRole.Parameters.AddWithValue("id", id);
            insertRole.Parameters.AddWithValue("role", role);
            await insertRole.ExecuteNonQueryAsync(ct);
        }
        return new UserRecord(id, username, request.DisplayName.Trim(), "", true, createdAt, roles.Order().ToArray());
    }

    private AuthResponse CreateAuthResponse(UserRecord user)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Username),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("userId", user.Id.ToString())
        };
        claims.AddRange(user.Roles.Order().Select(role => new Claim("roles", role)));
        var token = new JwtSecurityToken(_issuer, _audience, claims, now,
            now.AddSeconds(_expirationSeconds), new SigningCredentials(
                new SymmetricSecurityKey(_secret), SecurityAlgorithms.HmacSha256));
        return new AuthResponse(new JwtSecurityTokenHandler().WriteToken(token), user.ToView());
    }

    private static UserRecord ReadUser(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4),
        ReadCreatedAt(reader, 5),         (reader.GetString(6).Split(',', StringSplitOptions.RemoveEmptyEntries)));

    private static DateTimeOffset ReadCreatedAt(NpgsqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));

    private static void ValidateCredentials(string username, string displayName, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Trim().Length is < 3 or > 80)
            throw new ArgumentException("Username must contain between 3 and 80 characters.");
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 120)
            throw new ArgumentException("Display name is required and cannot exceed 120 characters.");
        if (string.IsNullOrWhiteSpace(password) || password.Length is < 8 or > 200)
            throw new ArgumentException("Password must contain between 8 and 200 characters.");
    }

    private static IReadOnlyCollection<string> ValidateRoles(IEnumerable<string>? input)
    {
        if (input is null) throw new ArgumentException("At least one role is required.");
        var roles = input.Select(role => role.Trim().ToUpperInvariant()).Distinct().ToArray();
        if (roles.Length == 0) throw new ArgumentException("At least one role is required.");
        var unknown = roles.FirstOrDefault(role => !Roles.All.Contains(role));
        if (unknown is not null) throw new ArgumentException($"Unknown role: {unknown}");
        return roles;
    }
}
