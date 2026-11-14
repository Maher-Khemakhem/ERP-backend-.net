using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupermarketErp.Api.Features.Auth;

namespace SupermarketErp.Api.Features.Ecommerce;

[ApiController]
[Route("api/store")]
[Authorize(Roles = Roles.Customer)]
public sealed class StorefrontOrderController(IStorefrontOrderService service) : ControllerBase
{
    [HttpGet("cart")]
    public async Task<ActionResult<CartView>> GetCart(CancellationToken ct) =>
        Ok(await service.GetCart(CustomerId(), ct));

    [HttpPost("cart/items")]
    public async Task<ActionResult<CartView>> AddCartItem(
        [FromBody] QuantityRequest request,
        CancellationToken ct) =>
        Ok(await service.AddCartItem(CustomerId(), request, ct));

    [HttpPut("cart/items/{productId:guid}")]
    public async Task<ActionResult<CartView>> ChangeCartItem(
        Guid productId,
        [FromBody] QuantityRequest request,
        CancellationToken ct)
    {
        if (productId != request.ProductId)
            throw new ArgumentException("Product ID in path and body must match.");
        return Ok(await service.ChangeCartItem(CustomerId(), request, ct));
    }

    [HttpDelete("cart/items/{productId:guid}")]
    public async Task<ActionResult<CartView>> RemoveCartItem(
        Guid productId,
        CancellationToken ct) =>
        Ok(await service.RemoveCartItem(CustomerId(), productId, ct));

    [HttpPost("checkout")]
    public async Task<ActionResult<OnlineOrderView>> Checkout(CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await service.Checkout(CustomerId(), ct));

    [HttpPost("checkout/{orderId:guid}/confirm-payment")]
    public async Task<ActionResult<OnlineOrderView>> ConfirmPayment(
        Guid orderId,
        CancellationToken ct) =>
        Ok(await service.ConfirmDemoPayment(CustomerId(), orderId, ct));

    [HttpGet("orders")]
    public async Task<ActionResult<IReadOnlyList<OnlineOrderView>>> ListOrders(CancellationToken ct) =>
        Ok(await service.ListCustomerOrders(CustomerId(), ct));

    [HttpGet("orders/{orderId:guid}")]
    public async Task<ActionResult<OnlineOrderView>> GetOrder(
        Guid orderId,
        CancellationToken ct) =>
        Ok(await service.GetCustomerOrder(CustomerId(), orderId, ct));

    [HttpPost("orders/{orderId:guid}/cancel")]
    public async Task<ActionResult<OnlineOrderView>> CancelOrder(
        Guid orderId,
        CancellationToken ct) =>
        Ok(await service.CancelOrder(CustomerId(), orderId, ct));

    private Guid CustomerId() =>
        Guid.TryParse(User.FindFirstValue("userId"), out var id)
            ? id
            : throw new InvalidOperationException("The authenticated token has no user ID.");
}
