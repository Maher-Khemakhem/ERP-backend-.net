using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupermarketErp.Api.Features.Auth;

namespace SupermarketErp.Api.Features.Inventory;

[ApiController]
public sealed class ProductImageController(IProductImageStorage storage) : ControllerBase
{
    [HttpPost("api/inventory/product-images")]
    [Authorize(Policy = Policies.InventoryWrite)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Upload(
        [FromForm] IFormFile file,
        CancellationToken ct) =>
        Ok(new { url = await storage.Save(file, ct) });

    [HttpGet("uploads/products/{filename}")]
    [AllowAnonymous]
    public async Task<IActionResult> Get(string filename, CancellationToken ct)
    {
        var file = await storage.Read(filename, ct);
        return file is null ? NotFound() : File(file.Value.Content, file.Value.ContentType);
    }
}
