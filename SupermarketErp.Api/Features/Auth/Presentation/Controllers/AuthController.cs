using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SupermarketErp.Api.Features.Auth;

[ApiController]
public sealed class AuthController(
    IAuthService service,
    LoginRateLimiter limiter) : ControllerBase
{
    [HttpGet("api/auth/setup-status")]
    [AllowAnonymous]
    public async Task<ActionResult<SetupStatus>> SetupStatus(CancellationToken ct) =>
        Ok(new SetupStatus(await service.SetupRequired(ct)));

    [HttpPost("api/auth/register")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Register(
        [FromBody] RegisterRequest request,
        CancellationToken ct) =>
        Ok(await service.RegisterFirstAdmin(request, ct));

    [HttpPost("api/auth/login")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Login(
        [FromBody] LoginRequest request,
        CancellationToken ct)
    {
        limiter.Check(ClientIp(), request.Username);
        return Ok(await service.Login(request, false, ct));
    }

    [HttpGet("api/auth/me")]
    [Authorize]
    public async Task<ActionResult<UserView>> Me(CancellationToken ct)
    {
        var username = User.FindFirstValue("sub") ?? User.Identity?.Name;
        if (username is null) return Unauthorized();

        var user = await service.FindByUsername(username, ct);
        return Ok(user ?? throw new ArgumentException("User not found"));
    }

    [HttpGet("api/auth/users")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<IReadOnlyList<UserView>>> ListUsers(CancellationToken ct) =>
        Ok(await service.ListUsers(ct));

    [HttpPost("api/auth/users")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<UserView>> CreateUser(
        [FromBody] CreateUserRequest request,
        CancellationToken ct) =>
        Ok(await service.CreateManagedUser(request, ct));

    [HttpPatch("api/auth/users/{userId:guid}/roles")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<UserView>> UpdateRoles(
        Guid userId,
        [FromBody] UpdateRolesRequest request,
        CancellationToken ct) =>
        Ok(await service.UpdateRoles(userId, request.Roles, ct));

    [HttpPatch("api/auth/users/{userId:guid}/enabled")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<UserView>> UpdateEnabled(
        Guid userId,
        [FromBody] UpdateEnabledRequest request,
        CancellationToken ct) =>
        Ok(await service.UpdateEnabled(userId, request.Enabled, ct));

    [HttpPost("api/store/auth/register")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> RegisterCustomer(
        [FromBody] RegisterRequest request,
        CancellationToken ct)
    {
        limiter.Check(ClientIp(), request.Username);
        return Ok(await service.RegisterCustomer(request, ct));
    }

    [HttpPost("api/store/auth/login")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> LoginCustomer(
        [FromBody] LoginRequest request,
        CancellationToken ct)
    {
        limiter.Check(ClientIp(), request.Username);
        return Ok(await service.Login(request, true, ct));
    }

    private string ClientIp()
    {
        var remote = HttpContext.Connection.RemoteIpAddress;
        if (remote is not null && System.Net.IPAddress.IsLoopback(remote))
        {
            var forwarded = HttpContext.Request.Headers["X-Forwarded-For"].ToString();
            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                var client = forwarded.Split(',').Last().Trim();
                if (!string.IsNullOrWhiteSpace(client)) return client;
            }
        }

        return remote?.ToString() ?? "unknown";
    }
}
