using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Inventory;

public sealed class ProductRepository(BusinessDbContext db) : IProductRepository
{
    public async Task<IReadOnlyList<ProductEntity>> List(CancellationToken ct) =>
        await db.Products.AsNoTracking().Include(x => x.Family).OrderBy(x => x.Name).ThenBy(x => x.Sku).ToListAsync(ct);

    public Task<ProductEntity?> Find(Guid productId, CancellationToken ct) =>
        db.Products.Include(x => x.Family).SingleOrDefaultAsync(x => x.Id == productId, ct);

    public Task<bool> FamilyExists(Guid familyId, CancellationToken ct) =>
        db.ProductFamilies.AnyAsync(x => x.Id == familyId, ct);

    public void Add(ProductEntity product) => db.Products.Add(product);
    public void Remove(ProductEntity product) => db.Products.Remove(product);
}
