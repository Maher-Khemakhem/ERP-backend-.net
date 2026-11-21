using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Inventory;

/// <summary>
/// Persistence and read models for the catalog families and the stock overview. The stock views are queries that
/// join products, stock levels and lots; stock movements themselves go through <see cref="IInventoryStock"/>.
/// </summary>
public sealed class InventoryRepository(BusinessDbContext db) : IInventoryRepository
{
    public async Task<IReadOnlyList<ProductFamilyEntity>> ListFamilies(CancellationToken ct) =>
        await db.ProductFamilies.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);

    public Task<bool> FamilyNameTaken(string name, CancellationToken ct) =>
        db.ProductFamilies.AnyAsync(x => x.Name.ToLower() == name.ToLower(), ct);

    public void Add(ProductFamilyEntity family) => db.ProductFamilies.Add(family);

    public async Task<IReadOnlyList<StockSummaryView>> ListStock(CancellationToken ct)
    {
        var products = await db.Products.AsNoTracking().Include(x => x.Family)
            .OrderBy(x => x.Name).ThenBy(x => x.Sku).ToListAsync(ct);
        var stock = await LoadStockRows(ct);
        return products.Select(product =>
        {
            var rows = stock.Where(x => x.ProductId == product.Id).ToArray();
            var onHand = rows.Sum(x => x.OnHand);
            var reserved = rows.Sum(x => x.Reserved);
            var available = onHand - reserved;
            var reorder = product.ReorderPoint ?? 0;
            var status = available <= 0 ? "OUT_OF_STOCK" : available <= reorder ? "LOW_STOCK" : "IN_STOCK";
            var earliest = rows.Where(x => x.OnHand != 0 && x.ExpiryDate is not null)
                .MinBy(x => x.ExpiryDate)?.ExpiryDate?.ToString("yyyy-MM-dd");
            return new StockSummaryView(product.Id, product.Sku, product.Name, product.FamilyId,
                product.Family?.Name, product.Family?.ImageUrl, product.ImageUrl, product.UnitType,
                product.Perishable, reorder, onHand, reserved, available,
                rows.Count(x => x.OnHand != 0), earliest, status);
        }).ToArray();
    }

    public async Task<StockLevelView?> GetStock(Guid productId, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == productId, ct);
        if (product is null) return null;
        var rows = (await LoadStockRows(ct)).Where(x => x.ProductId == productId && x.OnHand != 0)
            .OrderBy(x => x.ExpiryDate is null).ThenBy(x => x.ExpiryDate).ThenBy(x => x.ReceivedDate).ThenBy(x => x.LotId);
        var lots = rows.Select(x => new LotView(x.LotId.ToString(), x.LotNumber, productId.ToString(),
            x.OnHand, x.Reserved, x.OnHand - x.Reserved, x.ExpiryDate?.ToString("yyyy-MM-dd"),
            x.ReceivedDate.ToDateTime(TimeOnly.MinValue).ToString("yyyy-MM-dd'T'HH:mm"), x.UnitCost, x.SupplierReference)).ToArray();
        var total = lots.Sum(x => x.Quantity);
        var reserved = lots.Sum(x => x.ReservedQuantity);
        return new StockLevelView(productId, product.Name, total, reserved, total - reserved, lots);
    }

    private sealed record StockRow(Guid ProductId, Guid LotId, string LotNumber, decimal OnHand, decimal Reserved,
        DateOnly? ExpiryDate, DateOnly ReceivedDate, decimal UnitCost, string? SupplierReference);

    private async Task<IReadOnlyList<StockRow>> LoadStockRows(CancellationToken ct)
    {
        return await (from entry in db.LotEntries.AsNoTracking()
                      join lot in db.Lots.AsNoTracking() on entry.LotId equals lot.Id
                      join stock in db.StockLevels.AsNoTracking() on entry.StockLevelId equals stock.Id
                      select new StockRow(lot.ProductId, lot.Id, lot.LotNumber, entry.OnHandValue ?? 0,
                          entry.ReservedValue ?? 0, lot.ExpiryDate, lot.ReceivedDate, lot.UnitCost, lot.SupplierReference))
            .ToListAsync(ct);
    }
}
