using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace SupermarketErp.Api.Features.Ecommerce;

[ApiController]
[Route("api/store/products")]
[AllowAnonymous]
public sealed class StorefrontCatalogController(IStorefrontCatalog catalog) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<StorefrontProductView>>> List(
        [FromQuery] string? search,
        [FromQuery] Guid? familyId,
        CancellationToken ct) =>
        Ok(await catalog.List(search, familyId, ct));

    [HttpGet("{productId:guid}")]
    public async Task<ActionResult<StorefrontProductView>> Get(
        Guid productId,
        CancellationToken ct)
    {
        var product = await catalog.Get(productId, ct);
        return product is null ? NotFound() : Ok(product);
    }
}
