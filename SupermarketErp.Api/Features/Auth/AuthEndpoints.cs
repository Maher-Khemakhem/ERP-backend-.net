using System.Security.Claims;

namespace SupermarketErp.Api.Features.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/auth").WithTags("Authentication");
        auth.MapGet("/setup-status", async (AuthService service, CancellationToken ct) =>
            Results.Ok(new SetupStatus(await service.SetupRequired(ct))))
            .AllowAnonymous();
        auth.MapPost("/register", async (RegisterRequest request, AuthService service, CancellationToken ct) =>
            Results.Ok(await service.RegisterFirstAdmin(request, ct)))
            .AllowAnonymous();
        auth.MapPost("/login", async (LoginRequest request, HttpContext context, AuthService service,
            LoginRateLimiter limiter, CancellationToken ct) =>
        {
            limiter.Check(ClientIp(context), request.Username);
            return Results.Ok(await service.Login(request, false, ct));
        })
            .AllowAnonymous();
        auth.MapGet("/me", async (ClaimsPrincipal principal, AuthService service, CancellationToken ct) =>
        {
            var username = principal.FindFirstValue("sub") ?? principal.Identity?.Name;
            if (username is null) return Results.Unauthorized();
            var user = await service.FindByUsername(username, ct);
            return Results.Ok(user ?? throw new ArgumentException("User not found"));
        });
        auth.MapGet("/users", async (AuthService service, CancellationToken ct) => Results.Ok(await service.ListUsers(ct)))
            .RequireAuthorization(Policies.Admin);
        auth.MapPost("/users", async (CreateUserRequest request, AuthService service, CancellationToken ct) =>
            Results.Ok(await service.CreateManagedUser(request, ct)))
            .RequireAuthorization(Policies.Admin);
        auth.MapPatch("/users/{userId:guid}/roles", async (Guid userId, UpdateRolesRequest request, AuthService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateRoles(userId, request.Roles, ct)))
            .RequireAuthorization(Policies.Admin);
        auth.MapPatch("/users/{userId:guid}/enabled", async (Guid userId, UpdateEnabledRequest request, AuthService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateEnabled(userId, request.Enabled, ct)))
            .RequireAuthorization(Policies.Admin);

        var customerAuth = endpoints.MapGroup("/api/store/auth").WithTags("Storefront authentication");
        customerAuth.MapPost("/register", async (RegisterRequest request, HttpContext context, AuthService service,
            LoginRateLimiter limiter, CancellationToken ct) =>
        {
            limiter.Check(ClientIp(context), request.Username);
            return Results.Ok(await service.RegisterCustomer(request, ct));
        })
            .AllowAnonymous();
        customerAuth.MapPost("/login", async (LoginRequest request, HttpContext context, AuthService service,
            LoginRateLimiter limiter, CancellationToken ct) =>
        {
            limiter.Check(ClientIp(context), request.Username);
            return Results.Ok(await service.Login(request, true, ct));
        })
            .AllowAnonymous();
        return endpoints;
    }

    private static string ClientIp(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && System.Net.IPAddress.IsLoopback(remote))
        {
            var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                var client = forwarded.Split(',').Last().Trim();
                if (!string.IsNullOrWhiteSpace(client)) return client;
            }
        }

        return remote?.ToString() ?? "unknown";
    }
}
