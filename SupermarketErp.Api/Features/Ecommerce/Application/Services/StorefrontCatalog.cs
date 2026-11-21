using System.Globalization;
using SupermarketErp.Api.Features.Inventory;
using SupermarketErp.Api.Features.Pricing;

namespace SupermarketErp.Api.Features.Ecommerce;

/// <summary>The public storefront catalog, assembled from the Inventory and Pricing contracts.</summary>
public sealed class StorefrontCatalog(IInventoryStock inventory, IPricingCalculator pricing) : IStorefrontCatalog
{
    public async Task<IReadOnlyList<StorefrontProductView>> List(string? search, Guid? familyId, CancellationToken ct) =>
        await Map(await inventory.SearchOnlineProducts(search, familyId, ct), ct);

    public async Task<StorefrontProductView?> Get(Guid id, CancellationToken ct)
    {
        var product = await inventory.FindProduct(id, ct);
        if (product is null || !product.OnlineVisible) return null;
        return (await Map([product], ct)).SingleOrDefault();
    }

    private async Task<IReadOnlyList<StorefrontProductView>> Map(IReadOnlyList<ProductInfo> products, CancellationToken ct)
    {
        var ids = products.Select(x => x.Id).ToArray();
        var prices = await pricing.CurrentUnitPrices(ids, ct);
        var stock = await inventory.AvailableQuantities(ids, ct);
        return products.Where(x => prices.ContainsKey(x.Id)).Select(x =>
        {
            var available = stock.GetValueOrDefault(x.Id);
            return new StorefrontProductView(x.Id, x.Sku, x.Name, x.FamilyId, x.FamilyName, x.FamilyImageUrl,
                x.ImageUrl, x.UnitType, $"{prices[x.Id].ToString("0.000", CultureInfo.InvariantCulture)} TND",
                x.VatRate, available, available > 0);
        }).ToList();
    }
}
