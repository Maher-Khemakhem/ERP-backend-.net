using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Features.Inventory;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Pricing;

public sealed record CalculatedPrice(decimal Gross, decimal Net, decimal Vat);

public sealed class PricingService(
    BusinessDbContext db,
    IPricingRepository repository,
    IInventoryStock inventory) : IPricingCalculator
{
    public async Task<CalculatedPrice> CalculateLine(
        Guid productId,
        decimal quantity,
        string unitType,
        CancellationToken ct)
    {
        var product = await inventory.FindProduct(productId, ct)
            ?? throw new DomainValidationException($"Product not found: {productId}");

        if (!product.UnitType.Equals(unitType, StringComparison.Ordinal))
            throw new DomainValidationException("Requested unit type does not match the product.");

        var price = await repository.GetPrice(productId, ct);
        if (price?.UnitPrice is not decimal unitPrice)
            throw new DomainValidationException($"No price defined for product: {productId}");

        var now = DateTimeOffset.UtcNow;
        var discount = await repository.GetActiveDiscount(productId, now, ct);

        var net = decimal.Round(unitPrice * quantity, 3, MidpointRounding.AwayFromZero);
        if (discount is decimal percentage)
        {
            var rate = decimal.Round(percentage / 100m, 4, MidpointRounding.AwayFromZero);
            net = decimal.Round(net * (1m - rate), 3, MidpointRounding.AwayFromZero);
        }

        var vat = decimal.Round(net * product.VatRate / 100m, 3, MidpointRounding.AwayFromZero);
        return new CalculatedPrice(net + vat, net, vat);
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> CurrentUnitPrices(
        IReadOnlyCollection<Guid> productIds,
        CancellationToken ct)
    {
        if (productIds.Count == 0) return new Dictionary<Guid, decimal>();
        var ids = productIds.ToArray();
        var prices = await db.ProductPrices.AsNoTracking()
            .Where(x => ids.Contains(x.ProductId)).ToDictionaryAsync(x => x.ProductId, ct);
        var now = DateTimeOffset.UtcNow;
        var promotions = await db.Promotions.AsNoTracking()
            .Where(x => ids.Contains(x.ProductId) && x.Active && x.ValidFrom <= now && x.ValidUntil >= now)
            .OrderBy(x => x.ValidFrom).ToListAsync(ct);

        return prices.ToDictionary(
            x => x.Key,
            x =>
            {
                var discount = promotions.FirstOrDefault(p => p.ProductId == x.Key)?.DiscountPercent ?? 0;
                return decimal.Round((x.Value.UnitPrice ?? 0) * (1 - decimal.Round(discount / 100, 4)), 3, MidpointRounding.AwayFromZero);
            });
    }
}
