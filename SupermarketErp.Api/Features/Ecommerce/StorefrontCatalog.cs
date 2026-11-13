using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Infrastructure;
using SupermarketErp.Api.Features.Auth;

namespace SupermarketErp.Api.Features.Ecommerce;

public sealed record StorefrontProductView(Guid Id, string Sku, string Name, Guid? FamilyId,
    string? FamilyName, string? FamilyImageUrl, string? ImageUrl, string UnitType, string UnitPrice,
    decimal VatRate, decimal AvailableQuantity, bool InStock);

public sealed class StorefrontCatalog(BusinessDbContext db)
{
    public async Task<IReadOnlyList<StorefrontProductView>> List(string? search, Guid? familyId, CancellationToken ct)
    {
        if (search?.Length > 120) throw new ArgumentException("Search cannot exceed 120 characters.");
        var products = await db.Products.AsNoTracking().Include(x => x.Family)
            .Where(x => x.OnlineVisible && (familyId == null || x.FamilyId == familyId)
                && (search == null || x.Name.ToLower().Contains(search.Trim().ToLower())
                    || x.Sku.ToLower().Contains(search.Trim().ToLower())))
            .OrderBy(x => x.Name).ThenBy(x => x.Sku).ToListAsync(ct);
        return await Map(products, ct);
    }

    public async Task<StorefrontProductView?> Get(Guid id, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().Include(x => x.Family)
            .SingleOrDefaultAsync(x => x.Id == id && x.OnlineVisible, ct);
        if (product is null) return null;
        return (await Map(new[] { product }, ct)).SingleOrDefault();
    }

    private async Task<IReadOnlyList<StorefrontProductView>> Map(IEnumerable<ProductEntity> products, CancellationToken ct)
    {
        var ids = products.Select(x => x.Id).ToArray();
        var prices = await db.ProductPrices.AsNoTracking().Where(x => ids.Contains(x.ProductId)).ToDictionaryAsync(x => x.ProductId, ct);
        var now = DateTimeOffset.UtcNow;
        var promotions = await db.Promotions.AsNoTracking().Where(x => ids.Contains(x.ProductId) && x.Active
            && x.ValidFrom <= now && x.ValidUntil >= now).OrderBy(x => x.ValidFrom).ToListAsync(ct);
        var stock = await (from s in db.StockLevels.AsNoTracking()
                           join e in db.LotEntries.AsNoTracking() on s.Id equals e.StockLevelId
                           where ids.Contains(s.ProductId)
                           group e by s.ProductId into g
                           select new { ProductId = g.Key, Available = g.Sum(x => (x.OnHandValue ?? 0) - (x.ReservedValue ?? 0)) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Available, ct);
        return products.Where(x => prices.ContainsKey(x.Id)).Select(x =>
        {
            var discount = promotions.FirstOrDefault(p => p.ProductId == x.Id)?.DiscountPercent ?? 0;
            var amount = decimal.Round((prices[x.Id].UnitPrice ?? 0) * (1 - decimal.Round(discount / 100, 4)), 3, MidpointRounding.AwayFromZero);
            var available = stock.GetValueOrDefault(x.Id);
            return new StorefrontProductView(x.Id, x.Sku, x.Name, x.FamilyId, x.Family?.Name, x.Family?.ImageUrl,
                x.ImageUrl, x.UnitType, $"{amount.ToString("0.000", CultureInfo.InvariantCulture)} TND",
                x.VatRate ?? 19, available, available > 0);
        }).ToList();
    }
}

public static class StorefrontCatalogEndpoints
{
    public static IEndpointRouteBuilder MapStorefrontCatalog(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/store/products").WithTags("Storefront catalog");
        group.MapGet("", async (string? search, Guid? familyId, StorefrontCatalog catalog, CancellationToken ct) =>
            Results.Ok(await catalog.List(search, familyId, ct))).AllowAnonymous();
        group.MapGet("/{productId:guid}", async (Guid productId, StorefrontCatalog catalog, CancellationToken ct) =>
            await catalog.Get(productId, ct) is { } product ? Results.Ok(product) : Results.NotFound()).AllowAnonymous();
        return endpoints;
    }
}

