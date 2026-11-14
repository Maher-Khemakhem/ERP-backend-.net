using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupermarketErp.Api.Features.Auth;

namespace SupermarketErp.Api.Features.Inventory;

[ApiController]
[Route("api/inventory")]
public sealed class InventoryController(IInventoryService service) : ControllerBase
{
    [HttpGet("stock")]
    [Authorize(Policy = Policies.StaffRead)]
    public async Task<ActionResult<IReadOnlyList<StockSummaryView>>> ListStock(
        CancellationToken ct) =>
        Ok(await service.ListStock(ct));

    [HttpGet("stock/{productId:guid}")]
    [Authorize(Policy = Policies.StaffRead)]
    public async Task<ActionResult<StockLevelView>> GetStock(
        Guid productId,
        CancellationToken ct)
    {
        var stock = await service.GetStock(productId, ct);
        return stock is null ? NotFound() : Ok(stock);
    }

    [HttpPost("receive-stock")]
    [Authorize(Policy = Policies.InventoryWrite)]
    public async Task<ActionResult<ReceiveStockResponse>> ReceiveStock(
        [FromBody] ReceiveStockRequest request,
        CancellationToken ct)
    {
        await service.ReceiveStock(request, ct);
        return Ok(new ReceiveStockResponse("received"));
    }

    [HttpGet("product-families")]
    [Authorize(Roles = Roles.Admin + "," + Roles.Manager + "," + Roles.InventoryManager)]
    public async Task<ActionResult<IReadOnlyList<FamilyView>>> ListFamilies(
        CancellationToken ct) =>
        Ok(await service.ListFamilies(ct));

    [HttpPost("product-families")]
    [Authorize(Policy = Policies.InventoryWrite)]
    public async Task<IActionResult> CreateFamily(
        [FromBody] CreateFamilyRequest request,
        CancellationToken ct)
    {
        try
        {
            var id = await service.CreateFamily(request, ct);
            return Ok(new { familyId = id });
        }
        catch (InvalidOperationException)
        {
            return Conflict();
        }
    }
}
