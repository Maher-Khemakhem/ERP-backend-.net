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
