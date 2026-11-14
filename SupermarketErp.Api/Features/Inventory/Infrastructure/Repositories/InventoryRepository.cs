using SupermarketErp.Api.Features.Auth;
using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Inventory;

public sealed class InventoryRepository(BusinessDbContext db) : IInventoryRepository
{
    public async Task<IReadOnlyList<FamilyView>> ListFamilies(CancellationToken ct)
    {
        return await db.ProductFamilies.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new FamilyView(x.Id, x.Name, x.ImageUrl)).ToListAsync(ct);
    }

    public async Task<Guid> CreateFamily(CreateFamilyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Family name is required.");
        if (request.Name.Trim().Length > 160) throw new ArgumentException("Family name cannot exceed 160 characters.");
        if (request.ImageUrl?.Length > 500) throw new ArgumentException("Image URL cannot exceed 500 characters.");
        var name = request.Name.Trim();
        if (await db.ProductFamilies.AnyAsync(x => x.Name.ToLower() == name.ToLower(), ct))
            throw new InvalidOperationException("A product family with this name already exists.");
        var id = Guid.NewGuid();
        db.ProductFamilies.Add(new ProductFamilyEntity { Id = id, Name = name, ImageUrl = Normalize(request.ImageUrl) });
        await db.SaveChangesAsync(ct);
        return id;
    }

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

    public async Task ReceiveStock(ReceiveStockRequest request, CancellationToken ct)
    {
        if (request.QuantityValue <= 0) throw new ArgumentException("Quantity must be positive.");
        if (request.UnitCost is < 0) throw new ArgumentException("Unit cost cannot be negative.");
        var unitType = request.UnitType?.Trim().ToUpperInvariant();
        if (unitType is not ("EACH" or "WEIGHT")) throw new ArgumentException("Unit type must be EACH or WEIGHT.");
        if (unitType == "EACH" && request.QuantityValue != decimal.Truncate(request.QuantityValue))
            throw new ArgumentException("EACH quantities must be whole numbers.");
        var quantityValue = unitType == "WEIGHT"
            ? decimal.Round(request.QuantityValue, 3, MidpointRounding.AwayFromZero)
            : request.QuantityValue;

        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var product = await db.Products.SingleOrDefaultAsync(x => x.Id == request.ProductId, ct)
            ?? throw new ArgumentException($"Product not found: {request.ProductId}");
        if (!product.UnitType.Equals(unitType, StringComparison.Ordinal))
            throw new ArgumentException("Requested unit type does not match the product.");
        if (product.Perishable && request.ExpiryDate is null)
            throw new ArgumentException("Perishable products require an expiry date.");

        var stock = await db.StockLevels.SingleOrDefaultAsync(x => x.ProductId == request.ProductId, ct);
        if (stock is null)
        {
            stock = new StockLevelEntity { Id = Guid.NewGuid(), ProductId = request.ProductId, Version = 0 };
            db.StockLevels.Add(stock);
        }
        else stock.Version++;
        var lotNumber = string.IsNullOrWhiteSpace(request.LotNumber)
            ? $"LOT-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"
            : request.LotNumber.Trim();
        var lot = new LotEntity { Id = Guid.NewGuid(), LotNumber = lotNumber, ProductId = request.ProductId,
            ExpiryDate = request.ExpiryDate, ReceivedDate = request.ReceivedDate ?? DateOnly.FromDateTime(DateTime.Today),
            SupplierReference = Normalize(request.SupplierReference), UnitCost = request.UnitCost ?? 0m };
        db.Lots.Add(lot);
        db.LotEntries.Add(new LotEntryEntity { Id = Guid.NewGuid(), StockLevelId = stock.Id, LotId = lot.Id,
            OnHandValue = quantityValue, OnHandUnitType = Unit(unitType), ReservedValue = 0, ReservedUnitType = Unit(unitType) });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
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

    private static short Unit(string unit) => unit == "EACH" ? (short)0 : (short)1;
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

