using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupermarketErp.Api.Features.Auth;

namespace SupermarketErp.Api.Features.Pricing;

[ApiController]
[Route("api/pricing")]
public sealed class PricingController(IPricingManagementService service) : ControllerBase
{
    [HttpGet("prices")]
    [Authorize(Policy = Policies.StaffRead)]
    [ProducesResponseType<IReadOnlyList<PriceView>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PriceView>>> ListPrices(CancellationToken ct) =>
        Ok(await service.ListPrices(ct));

    [HttpGet("prices/{productId:guid}")]
    [Authorize(Policy = Policies.StaffRead)]
    [ProducesResponseType<PriceView>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PriceView>> GetPrice(Guid productId, CancellationToken ct)
    {
        var price = await service.GetPrice(productId, ct);
        return price is null ? NotFound() : Ok(price);
    }

    [HttpPost("prices")]
    [Authorize(Policy = Policies.InventoryWrite)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetPrice(
        [FromBody] SetPriceRequest request,
        CancellationToken ct)
    {
        await service.SetPrice(request, ct);
        return Ok(new { productId = request.ProductId });
    }

    [HttpDelete("prices/{productId:guid}")]
    [Authorize(Policy = Policies.InventoryWrite)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeletePrice(Guid productId, CancellationToken ct) =>
        await service.DeletePrice(productId, ct) ? Ok() : NotFound();

    [HttpGet("promotions")]
    [Authorize(Policy = Policies.StaffRead)]
    [ProducesResponseType<IReadOnlyList<PromotionView>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PromotionView>>> ListPromotions(
        CancellationToken ct) =>
        Ok(await service.ListPromotions(null, ct));

    [HttpGet("promotions/{productId:guid}")]
    [Authorize(Policy = Policies.StaffRead)]
    [ProducesResponseType<IReadOnlyList<PromotionView>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PromotionView>>> ListPromotions(
        Guid productId,
        CancellationToken ct) =>
        Ok(await service.ListPromotions(productId, ct));

    [HttpPost("promotions")]
    [Authorize(Policy = Policies.InventoryWrite)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> CreatePromotion(
        [FromBody] CreatePromotionRequest request,
        CancellationToken ct)
    {
        var promotionId = await service.CreatePromotion(request, ct);
        return Ok(new { promotionId });
    }

    [HttpDelete("promotions/{promotionId:guid}")]
    [Authorize(Policy = Policies.InventoryWrite)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeactivatePromotion(
        Guid promotionId,
        CancellationToken ct) =>
        await service.DeactivatePromotion(promotionId, ct) ? Ok() : NotFound();
}
