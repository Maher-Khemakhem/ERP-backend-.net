using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Inventory;

/// <inheritdoc cref="IInventoryStock"/>
public sealed class InventoryStock(BusinessDbContext db) : IInventoryStock
{
    private const decimal DefaultVatRate = 19m;

    // ---- products (read) -------------------------------------------------------------------

    public async Task<ProductInfo?> FindProduct(Guid productId, CancellationToken ct) =>
        (await Products().Where(x => x.Id == productId).SingleOrDefaultAsync(ct)) is { } product ? ToInfo(product) : null;

    public async Task<ProductInfo?> FindProductBySku(string sku, CancellationToken ct)
    {
        var wanted = sku.Trim().ToLower();
        return (await Products().Where(x => x.Sku.ToLower() == wanted).SingleOrDefaultAsync(ct)) is { } product
            ? ToInfo(product) : null;
    }

    public async Task<IReadOnlyDictionary<Guid, ProductInfo>> FindProducts(IReadOnlyCollection<Guid> productIds, CancellationToken ct)
    {
        if (productIds.Count == 0) return new Dictionary<Guid, ProductInfo>();
        var ids = productIds.ToArray();
        return (await Products().Where(x => ids.Contains(x.Id)).ToListAsync(ct)).ToDictionary(x => x.Id, ToInfo);
    }

    public async Task<IReadOnlyList<ProductInfo>> SearchOnlineProducts(string? search, Guid? familyId, CancellationToken ct)
    {
        if (search?.Length > 120) throw new DomainValidationException("Search cannot exceed 120 characters.");
        var text = search?.Trim().ToLower();
        var products = await Products()
            .Where(x => x.OnlineVisible && (familyId == null || x.FamilyId == familyId)
                && (text == null || x.Name.ToLower().Contains(text) || x.Sku.ToLower().Contains(text)))
            .OrderBy(x => x.Name).ThenBy(x => x.Sku).ToListAsync(ct);
        return products.Select(ToInfo).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> AvailableQuantities(IReadOnlyCollection<Guid> productIds, CancellationToken ct)
    {
        if (productIds.Count == 0) return new Dictionary<Guid, decimal>();
        var ids = productIds.ToArray();
        return await (from s in db.StockLevels.AsNoTracking()
                      join e in db.LotEntries.AsNoTracking() on s.Id equals e.StockLevelId
                      where ids.Contains(s.ProductId)
                      group e by s.ProductId into g
                      select new { ProductId = g.Key, Available = g.Sum(x => (x.OnHandValue ?? 0) - (x.ReservedValue ?? 0)) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Available, ct);
    }

    // ---- stock movements -------------------------------------------------------------------

    public async Task ReceiveLot(LotReceipt receipt, CancellationToken ct)
    {
        if (receipt.Quantity <= 0) throw new DomainValidationException("Quantity must be positive.");
        if (receipt.UnitCost < 0) throw new DomainValidationException("Unit cost cannot be negative.");
        var unit = NormalizeUnit(receipt.UnitType);
        if (unit == "EACH" && receipt.Quantity != decimal.Truncate(receipt.Quantity))
            throw new DomainValidationException("EACH quantities must be whole numbers.");
        var quantity = unit == "WEIGHT" ? decimal.Round(receipt.Quantity, 3, MidpointRounding.AwayFromZero) : receipt.Quantity;

        var product = await db.Products.SingleOrDefaultAsync(x => x.Id == receipt.ProductId, ct)
            ?? throw new DomainValidationException($"Product not found: {receipt.ProductId}");
        if (!product.UnitType.Equals(unit, StringComparison.Ordinal))
            throw new DomainValidationException("Requested unit type does not match the product.");
        if (product.Perishable && receipt.ExpiryDate is null)
            throw new DomainValidationException("Perishable products require an expiry date.");

        var stock = await LockStock(receipt.ProductId, ct);
        if (stock is null)
        {
            stock = new StockLevelEntity { Id = Guid.NewGuid(), ProductId = receipt.ProductId, Version = 0 };
            db.StockLevels.Add(stock);
        }
        else stock.Version++;

        var lot = new LotEntity
        {
            Id = Guid.NewGuid(),
            LotNumber = string.IsNullOrWhiteSpace(receipt.LotNumber)
                ? $"LOT-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}" : receipt.LotNumber.Trim(),
            ProductId = receipt.ProductId,
            ExpiryDate = receipt.ExpiryDate,
            ReceivedDate = receipt.ReceivedDate ?? DateOnly.FromDateTime(DateTime.Today),
            SupplierReference = string.IsNullOrWhiteSpace(receipt.SupplierReference) ? null : receipt.SupplierReference.Trim(),
            UnitCost = receipt.UnitCost
        };
        db.Lots.Add(lot);
        db.LotEntries.Add(new LotEntryEntity
        {
            Id = Guid.NewGuid(), StockLevelId = stock.Id, LotId = lot.Id,
            OnHandValue = quantity, OnHandUnitType = UnitCode(unit), ReservedValue = 0, ReservedUnitType = UnitCode(unit)
        });
        // Flush so a second lot for the same product, received later in the same transaction, finds this stock level.
        await db.SaveChangesAsync(ct);
    }

    public async Task<decimal> Sell(Guid productId, decimal quantity, string unitType, CancellationToken ct)
    {
        var stock = await LockStock(productId, ct)
            ?? throw new ConflictException($"No stock found for product: {productId}");
        var lots = await FefoLots(stock.Id, UnitCode(NormalizeUnit(unitType)), ct);

        var available = lots.Sum(x => x.Available);
        if (available < quantity)
            throw new ConflictException(
                $"Insufficient stock for product {productId}: requested {quantity}, available {available}.");

        var remaining = quantity;
        decimal cost = 0;
        foreach (var lot in lots)
        {
            if (remaining <= 0) break;
            if (lot.Available <= 0) continue;

            var take = Math.Min(remaining, lot.Available);
            cost = Money(cost + take * lot.UnitCost);
            lot.Entry.OnHandValue = lot.OnHand - take;
            remaining -= take;
        }

        stock.Version++;
        await RaiseLowStockIfNeeded(stock.Id, productId, ct);
        return cost;
    }

    public async Task Reserve(Guid productId, decimal quantity, string unitType, CancellationToken ct)
    {
        var stock = await RequireStock(productId, ct);
        var lots = await FefoLots(stock.Id, UnitCode(NormalizeUnit(unitType)), ct);
        if (lots.Sum(x => x.Available) < quantity)
            throw new ConflictException($"Insufficient available stock for product {productId}.");

        var remaining = quantity;
        foreach (var lot in lots)
        {
            var take = Math.Min(remaining, lot.Available);
            if (take <= 0) continue;
            lot.Entry.ReservedValue = lot.Reserved + take;
            remaining -= take;
            if (remaining <= 0) break;
        }
        stock.Version++;
    }

    public async Task Release(Guid productId, decimal quantity, string unitType, CancellationToken ct)
    {
        var stock = await RequireStock(productId, ct);
        var lots = await FefoLots(stock.Id, UnitCode(NormalizeUnit(unitType)), ct);
        if (lots.Sum(x => x.Reserved) < quantity)
            throw new ConflictException("Reserved stock is insufficient to release.");

        var remaining = quantity;
        foreach (var lot in lots.Where(x => x.Reserved > 0))
        {
            var take = Math.Min(remaining, lot.Reserved);
            lot.Entry.ReservedValue = lot.Reserved - take;
            remaining -= take;
            if (remaining <= 0) break;
        }
        stock.Version++;
    }

    public async Task<decimal> ConsumeReserved(Guid productId, decimal quantity, string unitType, CancellationToken ct)
    {
        var stock = await RequireStock(productId, ct);
        var lots = await FefoLots(stock.Id, UnitCode(NormalizeUnit(unitType)), ct);
        if (lots.Sum(x => x.Reserved) < quantity)
            throw new ConflictException("Reserved stock is insufficient.");

        var remaining = quantity;
        decimal cost = 0;
        foreach (var lot in lots.Where(x => x.Reserved > 0))
        {
            var take = Math.Min(remaining, lot.Reserved);
            lot.Entry.OnHandValue = lot.OnHand - take;
            lot.Entry.ReservedValue = lot.Reserved - take;
            cost = Money(cost + take * lot.UnitCost);
            remaining -= take;
            if (remaining <= 0) break;
        }
        stock.Version++;
        await RaiseLowStockIfNeeded(stock.Id, productId, ct);
        return cost;
    }

    // ---- internals -------------------------------------------------------------------------

    private IQueryable<ProductEntity> Products() => db.Products.AsNoTracking().Include(x => x.Family);

    private static ProductInfo ToInfo(ProductEntity x) => new(x.Id, x.Sku, x.Name, x.FamilyId, x.Family?.Name,
        x.Family?.ImageUrl, x.ImageUrl, x.UnitType, x.Perishable, x.ReorderPoint ?? 0, x.VatRate ?? DefaultVatRate, x.OnlineVisible);

    private async Task<StockLevelEntity> RequireStock(Guid productId, CancellationToken ct) =>
        await LockStock(productId, ct) ?? throw new ConflictException($"No stock exists for product {productId}.");

    /// <summary>
    /// Loads a product's stock level and takes an update lock on it for the rest of the transaction. Sales of the same
    /// product then queue up one behind the other instead of all reading first and deadlocking when they write.
    /// </summary>
    private async Task<StockLevelEntity?> LockStock(Guid productId, CancellationToken ct) =>
        (await db.StockLevels
            .FromSqlInterpolated($"SELECT * FROM inventory.stock_level WITH (UPDLOCK, HOLDLOCK) WHERE product_id = {productId}")
            .ToListAsync(ct)).SingleOrDefault();

    private sealed record Lot(LotEntryEntity Entry, decimal OnHand, decimal Reserved, decimal UnitCost)
    {
        public decimal Available => OnHand - Reserved;
    }

    /// <summary>Tracked lot entries of one unit type, first-expired-first-out (no expiry last, then oldest received).</summary>
    private async Task<List<Lot>> FefoLots(Guid stockLevelId, short unitCode, CancellationToken ct) =>
        await (from entry in db.LotEntries
               join lot in db.Lots on entry.LotId equals lot.Id
               where entry.StockLevelId == stockLevelId && entry.OnHandUnitType == unitCode
               orderby lot.ExpiryDate == null, lot.ExpiryDate, lot.ReceivedDate, lot.Id
               select new Lot(entry, entry.OnHandValue ?? 0m, entry.ReservedValue ?? 0m, lot.UnitCost))
            .ToListAsync(ct);

    /// <summary>
    /// Flushes first: EF queries do not see unsaved changes, so without this the totals would describe the stock
    /// from before the movement and the check would lag one sale behind.
    /// </summary>
    private async Task RaiseLowStockIfNeeded(Guid stockLevelId, Guid productId, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        var available = await db.LotEntries.Where(x => x.StockLevelId == stockLevelId)
            .Select(x => (x.OnHandValue ?? 0m) - (x.ReservedValue ?? 0m)).SumAsync(ct);
        var reorderPoint = await db.Products.AsNoTracking().Where(x => x.Id == productId)
            .Select(x => x.ReorderPoint ?? 0).SingleAsync(ct);
        if (available <= reorderPoint)
            await OutboxWriter.Write(db, "LowStockThresholdReached", productId, ct);
    }

    private static string NormalizeUnit(string unit)
    {
        var normalized = unit?.Trim().ToUpperInvariant();
        return normalized is "EACH" or "WEIGHT" ? normalized : throw new DomainValidationException("Unit type must be EACH or WEIGHT.");
    }

    private static short UnitCode(string unit) => unit == "EACH" ? (short)0 : (short)1;
    private static decimal Money(decimal value) => decimal.Round(value, 3, MidpointRounding.AwayFromZero);
}
