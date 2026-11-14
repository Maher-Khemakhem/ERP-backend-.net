using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Inventory;

public sealed class ProductRepository(BusinessDbContext db) : IProductRepository
{
    public async Task<IReadOnlyList<ProductView>> List(CancellationToken ct)
    {
        var products = await db.Products.AsNoTracking().Include(x => x.Family)
            .OrderBy(x => x.Name).ThenBy(x => x.Sku)
            .ToListAsync(ct);
        return products.Select(ToView).ToArray();
    }

    public async Task<ProductView?> Get(Guid productId, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().Include(x => x.Family)
            .SingleOrDefaultAsync(x => x.Id == productId, ct);
        return product is null ? null : ToView(product);
    }

    public async Task<Guid> Create(CreateProductRequest request, CancellationToken ct)
    {
        Validate(request);
        if (request.FamilyId is not null
            && !await db.ProductFamilies.AnyAsync(x => x.Id == request.FamilyId, ct))
            throw new ArgumentException($"Product family not found: {request.FamilyId}");

        var product = new ProductEntity
        {
            Id = Guid.NewGuid(), Sku = request.Sku.Trim(), Name = request.Name.Trim(),
            FamilyId = request.FamilyId, ImageUrl = NormalizeOptional(request.ImageUrl),
            UnitType = request.UnitType.Trim().ToUpperInvariant(), Perishable = request.Perishable,
            ReorderPoint = request.ReorderPoint, VatRate = request.VatRate ?? 19m, OnlineVisible = true
        };
        db.Products.Add(product);
        await db.SaveChangesAsync(ct);
        return product.Id;
    }

    public async Task<bool> Delete(Guid productId, CancellationToken ct)
    {
        var product = await db.Products.SingleOrDefaultAsync(x => x.Id == productId, ct);
        if (product is null) return false;
        db.Products.Remove(product);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static ProductView ToView(ProductEntity x) => new(
        x.Id, x.Sku, x.Name, x.FamilyId, x.Family?.Name, x.Family?.ImageUrl, x.ImageUrl,
        x.UnitType, x.Perishable, x.ReorderPoint ?? 0, x.VatRate ?? 19m);

    private static void Validate(CreateProductRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Sku)) throw new ArgumentException("SKU is required.");
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Name is required.");
        if (request.ImageUrl?.Length > 500) throw new ArgumentException("Image URL cannot exceed 500 characters.");
        if (request.UnitType is not null && request.UnitType.Trim().ToUpperInvariant() is not ("EACH" or "WEIGHT"))
            throw new ArgumentException("Unit type must be EACH or WEIGHT.");
        if (request.ReorderPoint < 0) throw new ArgumentException("Reorder point cannot be negative.");
        if (request.VatRate is < 0 or > 100) throw new ArgumentException("VAT rate must be between 0 and 100.");
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
