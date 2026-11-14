using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Pricing;

public sealed class PricingRepository(BusinessDbContext db) : IPricingRepository
{
    public async Task<IReadOnlyList<ProductPriceEntity>> ListPrices(CancellationToken ct) =>
        await db.ProductPrices.AsNoTracking().OrderBy(x => x.ProductId).ToListAsync(ct);

    public Task<ProductPriceEntity?> GetPrice(Guid productId, CancellationToken ct) =>
        db.ProductPrices.AsNoTracking().SingleOrDefaultAsync(x => x.ProductId == productId, ct);

    public Task<decimal?> GetActiveDiscount(
        Guid productId,
        DateTimeOffset at,
        CancellationToken ct) =>
        db.Promotions.AsNoTracking()
            .Where(x => x.ProductId == productId &&
                        x.Active &&
                        x.ValidFrom <= at &&
                        x.ValidUntil >= at)
            .OrderBy(x => x.ValidFrom)
            .Select(x => (decimal?)x.DiscountPercent)
            .FirstOrDefaultAsync(ct);

    public async Task UpsertPrice(Guid productId, decimal amount, CancellationToken ct)
    {
        var price = await db.ProductPrices
            .SingleOrDefaultAsync(x => x.ProductId == productId, ct);
        if (price is null)
            db.ProductPrices.Add(new ProductPriceEntity
            {
                Id = Guid.NewGuid(),
                ProductId = productId,
                UnitPrice = amount
            });
        else
            price.UnitPrice = amount;

        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeletePrice(Guid productId, CancellationToken ct)
    {
        var price = await db.ProductPrices
            .SingleOrDefaultAsync(x => x.ProductId == productId, ct);
        if (price is null) return false;

        db.ProductPrices.Remove(price);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<PromotionEntity>> ListPromotions(
        Guid? productId,
        CancellationToken ct) =>
        await db.Promotions.AsNoTracking()
            .Where(x => productId == null || x.ProductId == productId)
            .OrderByDescending(x => x.ValidFrom)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);

    public async Task<Guid> AddPromotion(PromotionEntity promotion, CancellationToken ct)
    {
        db.Promotions.Add(promotion);
        await db.SaveChangesAsync(ct);
        return promotion.Id;
    }

    public async Task<bool> DeactivatePromotion(Guid promotionId, CancellationToken ct)
    {
        var promotion = await db.Promotions
            .SingleOrDefaultAsync(x => x.Id == promotionId, ct);
        if (promotion is null) return false;

        promotion.Active = false;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
