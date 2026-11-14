using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupermarketErp.Api.Features.Auth;

namespace SupermarketErp.Api.Features.Ecommerce;

[ApiController]
public sealed class StripeCheckoutController(IStripeCheckoutService service) : ControllerBase
{
    [HttpGet("api/store/stripe/config")]
    [Authorize(Roles = Roles.Customer)]
    public ActionResult<StripeConfigurationView> Configuration() =>
        Ok(service.Configuration());

    [HttpGet("api/store/stripe/orders/{orderId:guid}/quote")]
    [Authorize(Roles = Roles.Customer)]
    public async Task<ActionResult<StripeQuote>> Quote(
        Guid orderId,
        CancellationToken ct) =>
        Ok(await service.Quote(CustomerId(), orderId, ct));

    [HttpPost("api/store/stripe/orders/{orderId:guid}/checkout")]
    [Authorize(Roles = Roles.Customer)]
    public async Task<ActionResult<StripeCheckoutResult>> Create(
        Guid orderId,
        CancellationToken ct) =>
        Ok(await service.Create(CustomerId(), orderId, ct));

    [HttpGet("api/store/stripe/orders/{orderId:guid}/status")]
    [Authorize(Roles = Roles.Customer)]
    public async Task<IActionResult> Reconcile(
        Guid orderId,
        CancellationToken ct) =>
        Ok(new { status = await service.Reconcile(CustomerId(), orderId, ct) });

    [HttpPost("api/store/stripe/webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        await service.Webhook(body, Request.Headers["Stripe-Signature"].FirstOrDefault(), ct);
        return Ok();
    }

    private Guid CustomerId() =>
        Guid.TryParse(User.FindFirstValue("userId"), out var id)
            ? id
            : throw new InvalidOperationException("The authenticated token has no user ID.");
}
