using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Pricing;

public sealed record CalculatedPrice(decimal Gross, decimal Net, decimal Vat);

public sealed class PricingService(
    BusinessDbContext db,
    IPricingRepository repository) : IPricingCalculator
{
    public async Task<CalculatedPrice> CalculateLine(
        Guid productId,
        decimal quantity,
        string unitType,
        CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == productId, ct)
            ?? throw new ArgumentException($"Product not found: {productId}");

        if (!product.UnitType.Equals(unitType, StringComparison.Ordinal))
            throw new ArgumentException("Requested unit type does not match the product.");

        var price = await repository.GetPrice(productId, ct);
        if (price?.UnitPrice is not decimal unitPrice)
            throw new ArgumentException($"No price defined for product: {productId}");

        var now = DateTimeOffset.UtcNow;
        var discount = await repository.GetActiveDiscount(productId, now, ct);

        var net = decimal.Round(unitPrice * quantity, 3, MidpointRounding.AwayFromZero);
        if (discount is decimal percentage)
        {
            var rate = decimal.Round(percentage / 100m, 4, MidpointRounding.AwayFromZero);
            net = decimal.Round(net * (1m - rate), 3, MidpointRounding.AwayFromZero);
        }

        var vat = decimal.Round(net * (product.VatRate ?? 19m) / 100m, 3, MidpointRounding.AwayFromZero);
        return new CalculatedPrice(net + vat, net, vat);
    }
}
