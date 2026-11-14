using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Pos;

public sealed class InventorySaleService(BusinessDbContext db)
{
    public async Task<decimal> Sell(
        Guid productId,
        decimal requested,
        short unit,
        CancellationToken ct)
    {
        var stock = await db.StockLevels
            .SingleOrDefaultAsync(x => x.ProductId == productId, ct)
            ?? throw new InvalidOperationException($"No stock found for product: {productId}");

        var lots = await (
            from entry in db.LotEntries
            join lot in db.Lots on entry.LotId equals lot.Id
            where entry.StockLevelId == stock.Id && entry.OnHandUnitType == unit
            orderby lot.ExpiryDate == null, lot.ExpiryDate, lot.ReceivedDate, lot.Id
            select new { Entry = entry, Lot = lot })
            .ToListAsync(ct);

        var available = lots.Sum(x => (x.Entry.OnHandValue ?? 0m) - (x.Entry.ReservedValue ?? 0m));
        if (available < requested)
            throw new InvalidOperationException(
                $"Insufficient stock for product {productId}: requested {requested}, available {available}.");

        var remaining = requested;
        decimal cost = 0;
        foreach (var lot in lots)
        {
            if (remaining <= 0) break;

            var availableInLot = (lot.Entry.OnHandValue ?? 0m) - (lot.Entry.ReservedValue ?? 0m);
            if (availableInLot <= 0) continue;

            var take = Math.Min(remaining, availableInLot);
            cost = decimal.Round(cost + take * lot.Lot.UnitCost, 3, MidpointRounding.AwayFromZero);
            lot.Entry.OnHandValue = (lot.Entry.OnHandValue ?? 0m) - take;
            remaining -= take;
        }

        stock.Version++;
        var onHand = await db.LotEntries
            .Where(x => x.StockLevelId == stock.Id)
            .Select(x => (x.OnHandValue ?? 0m) - (x.ReservedValue ?? 0m))
            .SumAsync(ct);
        var product = await db.Products.AsNoTracking()
            .SingleAsync(x => x.Id == productId, ct);

        if (onHand <= product.ReorderPoint)
        {
            await db.SaveChangesAsync(ct);
            await OutboxWriter.Write(db, "LowStockThresholdReached", productId, ct);
        }

        return cost;
    }
}
