using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupermarketErp.Api.Features.Auth;

namespace SupermarketErp.Api.Features.Ecommerce;

[ApiController]
[Route("api/online-orders")]
[Authorize(Roles = Roles.Admin + "," + Roles.Manager + "," + Roles.Cashier)]
public sealed class StaffOnlineOrderController(IStorefrontOrderService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<StaffOnlineOrderView>>> List(CancellationToken ct) =>
        Ok(await service.ListStaffOrders(ct));

    [HttpGet("{orderId:guid}")]
    public async Task<ActionResult<StaffOnlineOrderView>> Get(
        Guid orderId,
        CancellationToken ct) =>
        Ok(await service.GetStaffOrder(orderId, ct));

    [HttpPatch("{orderId:guid}/status")]
    public async Task<ActionResult<StaffOnlineOrderView>> AdvanceStatus(
        Guid orderId,
        [FromBody] FulfillmentStatusRequest request,
        CancellationToken ct) =>
        Ok(await service.AdvanceStatus(orderId, request.Status, ct));

    [HttpPost("{orderId:guid}/pickup")]
    public async Task<ActionResult<StaffOnlineOrderView>> CompletePickup(
        Guid orderId,
        [FromBody] PickupRequest request,
        CancellationToken ct) =>
        Ok(await service.CompletePickup(
            orderId,
            request.Code,
            User.FindFirstValue("sub") ?? "staff",
            ct));

    [HttpPost("{orderId:guid}/cancel")]
    public async Task<ActionResult<StaffOnlineOrderView>> Cancel(
        Guid orderId,
        CancellationToken ct) =>
        Ok(await service.CancelStaffOrder(orderId, ct));
}
