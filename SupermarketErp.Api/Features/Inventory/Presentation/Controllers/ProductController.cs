using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using SupermarketErp.Api.Features.Auth;

namespace SupermarketErp.Api.Features.Inventory;

[ApiController]
[Route("api/inventory/products")]
public sealed class ProductController(IProductService service) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Policies.StaffRead)]
    public async Task<ActionResult<IReadOnlyList<ProductView>>> List(CancellationToken ct) =>
        Ok(await service.List(ct));

    [HttpGet("{productId:guid}")]
    [Authorize(Policy = Policies.StaffRead)]
    public async Task<ActionResult<ProductView>> Get(Guid productId, CancellationToken ct)
    {
        var product = await service.Get(productId, ct);
        return product is null ? NotFound() : Ok(product);
    }

    [HttpPost]
    [Authorize(Policy = Policies.InventoryWrite)]
    public async Task<IActionResult> Create(
        [FromBody] CreateProductRequest request,
        CancellationToken ct) =>
        Ok(new { productId = await service.Create(request, ct) });

    [HttpDelete("{productId:guid}")]
    [Authorize(Policy = Policies.InventoryWrite)]
    public async Task<IActionResult> Delete(Guid productId, CancellationToken ct)
    {
        try
        {
            return await service.Delete(productId, ct) ? NoContent() : NotFound();
        }
        catch (SqlException ex) when (ex.Number == 547)
        {
            return Conflict();
        }
    }
}
