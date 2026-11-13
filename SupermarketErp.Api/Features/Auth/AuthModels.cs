namespace SupermarketErp.Api.Features.Auth;

using SupermarketErp.Api.Infrastructure;

public sealed record LoginRequest(string Username, string Password);
public sealed record RegisterRequest(string Username, string DisplayName, string Password);
public sealed record CreateUserRequest(string Username, string DisplayName, string Password, HashSet<string> Roles);
public sealed record UpdateRolesRequest(HashSet<string> Roles);
public sealed record UpdateEnabledRequest(bool Enabled);
public sealed record SetupStatus(bool SetupRequired);
public sealed record UserView(Guid Id, string Username, string DisplayName, IReadOnlyList<string> Roles, bool Enabled, string CreatedAt);
public sealed record AuthResponse(string Token, UserView User);

internal sealed record UserRecord(
    Guid Id,
    string Username,
    string DisplayName,
    string PasswordHash,
    bool Enabled,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Roles)
{
    public UserView ToView() => new(Id, Username, DisplayName, Roles, Enabled, JavaInstantFormat.Format(CreatedAt));
}

public sealed class InvalidCredentialsException : Exception { }

internal sealed class AuthUser
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long Version { get; set; }
    public List<AuthUserRole> Roles { get; set; } = [];
}

internal sealed class AuthUserRole
{
    public Guid UserId { get; set; }
    public string Role { get; set; } = "";
    public AuthUser User { get; set; } = null!;
}
