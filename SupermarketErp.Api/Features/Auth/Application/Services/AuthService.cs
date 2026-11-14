using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace SupermarketErp.Api.Features.Auth;

public sealed class AuthService(
    AuthDbContext db,
    IConfiguration configuration) : IAuthService
{
    private readonly string _issuer = configuration["JWT_ISSUER"] ?? "supermarket-erp";
    private readonly string _audience = configuration["JWT_AUDIENCE"] ?? "supermarket-web";
    private readonly byte[] _secret = Encoding.UTF8.GetBytes(configuration["JWT_SECRET"]!);
    private readonly int _expirationSeconds = int.TryParse(configuration["JWT_EXPIRATION_SECONDS"], out var seconds)
        ? seconds
        : 900;

    public async Task<bool> SetupRequired(CancellationToken ct) => !await db.Users.AnyAsync(ct);

    public async Task<AuthResponse> RegisterFirstAdmin(RegisterRequest request, CancellationToken ct)
    {
        ValidateCredentials(request.Username, request.DisplayName, request.Password);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            "EXEC sp_getapplock @Resource = 'supermarket-erp-first-admin', @LockMode = 'Exclusive', @LockOwner = 'Transaction'",
            ct);
        if (await db.Users.AnyAsync(ct))
            throw new InvalidOperationException("The initial administrator has already been created.");

        var user = InsertUser(request, [Roles.Admin]);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return CreateAuthResponse(ToRecord(user));
    }

    public async Task<AuthResponse> RegisterCustomer(RegisterRequest request, CancellationToken ct)
    {
        ValidateCredentials(request.Username, request.DisplayName, request.Password);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var user = InsertUser(request, [Roles.Customer]);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return CreateAuthResponse(ToRecord(user));
    }

    public async Task<AuthResponse> Login(LoginRequest request, bool customerOnly, CancellationToken ct)
    {
        var user = await FindEntityByUsername(request.Username, ct);
        if (user is null)
            throw new InvalidCredentialsException();

        var record = ToRecord(user);
        if (!record.Enabled || (customerOnly && !record.Roles.Contains(Roles.Customer))
            || !BCrypt.Net.BCrypt.Verify(request.Password, record.PasswordHash))
        {
            throw new InvalidCredentialsException();
        }

        return CreateAuthResponse(record);
    }

    public async Task<UserView?> FindByUsername(string username, CancellationToken ct)
    {
        var user = await FindEntityByUsername(username, ct);
        return user is null ? null : ToRecord(user).ToView();
    }

    public async Task<IReadOnlyList<UserView>> ListUsers(CancellationToken ct) =>
        (await db.Users.AsNoTracking().Include(x => x.Roles).OrderBy(x => x.Username).ToListAsync(ct))
        .Select(user => ToRecord(user).ToView())
        .ToArray();

    public async Task<UserView> CreateManagedUser(CreateUserRequest request, CancellationToken ct)
    {
        ValidateCredentials(request.Username, request.DisplayName, request.Password);
        var roles = ValidateRoles(request.Roles);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var user = InsertUser(
            new RegisterRequest(request.Username, request.DisplayName, request.Password), roles);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ToRecord(user).ToView();
    }

    public async Task<UserView> UpdateRoles(Guid userId, HashSet<string> rolesInput, CancellationToken ct)
    {
        var roles = ValidateRoles(rolesInput);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var user = await db.Users.Include(x => x.Roles).SingleOrDefaultAsync(x => x.Id == userId, ct)
            ?? throw new ArgumentException($"User not found: {userId}");
        user.Roles.Clear();
        foreach (var role in roles)
            user.Roles.Add(new AuthUserRole { UserId = user.Id, Role = role });
        user.Version++;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ToRecord(user).ToView();
    }

    public async Task<UserView> UpdateEnabled(Guid userId, bool enabled, CancellationToken ct)
    {
        var user = await db.Users.Include(x => x.Roles).SingleOrDefaultAsync(x => x.Id == userId, ct)
            ?? throw new ArgumentException($"User not found: {userId}");
        user.Enabled = enabled;
        user.Version++;
        await db.SaveChangesAsync(ct);
        return ToRecord(user).ToView();
    }

    private Task<AuthUser?> FindEntityByUsername(string username, CancellationToken ct) =>
        db.Users.AsNoTracking()
            .Include(x => x.Roles)
            .SingleOrDefaultAsync(x => x.Username == username.Trim().ToLowerInvariant(), ct);

    private AuthUser InsertUser(RegisterRequest request, IReadOnlyCollection<string> roles)
    {
        var user = new AuthUser
        {
            Id = Guid.NewGuid(),
            Username = request.Username.Trim().ToLowerInvariant(),
            DisplayName = request.DisplayName.Trim(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            Version = 0
        };
        foreach (var role in roles)
            user.Roles.Add(new AuthUserRole { UserId = user.Id, Role = role });
        db.Users.Add(user);
        return user;
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

    private static UserRecord ToRecord(AuthUser user) => new(
        user.Id, user.Username, user.DisplayName, user.PasswordHash, user.Enabled,
        user.CreatedAt, user.Roles.Select(role => role.Role).ToArray());

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
