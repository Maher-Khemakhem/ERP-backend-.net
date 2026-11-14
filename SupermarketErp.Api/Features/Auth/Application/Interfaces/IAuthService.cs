namespace SupermarketErp.Api.Features.Auth;

public interface IAuthService
{
    Task<bool> SetupRequired(CancellationToken ct);
    Task<AuthResponse> RegisterFirstAdmin(RegisterRequest request, CancellationToken ct);
    Task<AuthResponse> RegisterCustomer(RegisterRequest request, CancellationToken ct);
    Task<AuthResponse> Login(LoginRequest request, bool customerOnly, CancellationToken ct);
    Task<UserView?> FindByUsername(string username, CancellationToken ct);
    Task<IReadOnlyList<UserView>> ListUsers(CancellationToken ct);
    Task<UserView> CreateManagedUser(CreateUserRequest request, CancellationToken ct);
    Task<UserView> UpdateRoles(Guid userId, HashSet<string> roles, CancellationToken ct);
    Task<UserView> UpdateEnabled(Guid userId, bool enabled, CancellationToken ct);
}
