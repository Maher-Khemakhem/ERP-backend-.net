using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Features.Auth;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Pricing;

public sealed record SetPriceRequest(Guid ProductId, decimal UnitPrice);
public sealed record PriceView(Guid ProductId, string UnitPrice);
public sealed record CreatePromotionRequest(Guid ProductId, decimal DiscountPercent,
    DateTimeOffset ValidFrom, DateTimeOffset ValidUntil);
public sealed record PromotionView(Guid Id, Guid ProductId, decimal DiscountPercent,
    DateTimeOffset ValidFrom, DateTimeOffset ValidUntil, bool Active);

public static class PricingEndpoints
{
    public static IEndpointRouteBuilder MapPricingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var pricing = endpoints.MapGroup("/api/pricing").WithTags("Pricing");
        pricing.MapGet("/prices", async (PricingRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListPrices(ct)))
            .RequireAuthorization(Policies.StaffRead);
        pricing.MapGet("/prices/{productId:guid}", async (Guid productId, PricingRepository repository, CancellationToken ct) =>
            await repository.GetPrice(productId, ct) is { } price ? Results.Ok(price) : Results.NotFound())
            .RequireAuthorization(Policies.StaffRead);
        pricing.MapPost("/prices", async (SetPriceRequest request, PricingRepository repository, CancellationToken ct) =>
        {
            await repository.SetPrice(request, ct);
            return Results.Ok(new { productId = request.ProductId });
        }).RequireAuthorization(Policies.InventoryWrite);
        pricing.MapDelete("/prices/{productId:guid}", async (Guid productId, PricingRepository repository, CancellationToken ct) =>
            await repository.DeletePrice(productId, ct) ? Results.Ok() : Results.NotFound())
            .RequireAuthorization(Policies.InventoryWrite);

        pricing.MapGet("/promotions", async (PricingRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListPromotions(null, ct)))
            .RequireAuthorization(Policies.StaffRead);
        pricing.MapGet("/promotions/{productId:guid}", async (Guid productId, PricingRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListPromotions(productId, ct)))
            .RequireAuthorization(Policies.StaffRead);
        pricing.MapPost("/promotions", async (CreatePromotionRequest request, PricingRepository repository, CancellationToken ct) =>
        {
            var promotionId = await repository.CreatePromotion(request, ct);
            return Results.Ok(new { promotionId });
        }).RequireAuthorization(Policies.InventoryWrite);
        pricing.MapDelete("/promotions/{promotionId:guid}", async (Guid promotionId, PricingRepository repository, CancellationToken ct) =>
            await repository.DeactivatePromotion(promotionId, ct) ? Results.Ok() : Results.NotFound())
            .RequireAuthorization(Policies.InventoryWrite);
        return endpoints;
    }
}

public sealed class PricingRepository(BusinessDbContext db)
{
    public async Task<IReadOnlyList<PriceView>> ListPrices(CancellationToken ct)
    {
        var prices = await db.ProductPrices.AsNoTracking().OrderBy(x => x.ProductId).ToListAsync(ct);
        return prices.Select(x => ToPrice(x.ProductId, x.UnitPrice ?? 0)).ToList();
    }

    public async Task<PriceView?> GetPrice(Guid productId, CancellationToken ct)
    {
        var price = await db.ProductPrices.AsNoTracking().SingleOrDefaultAsync(x => x.ProductId == productId, ct);
        return price is null ? null : ToPrice(price.ProductId, price.UnitPrice ?? 0);
    }

    public async Task SetPrice(SetPriceRequest request, CancellationToken ct)
    {
        if (request.ProductId == Guid.Empty) throw new ArgumentException("Product ID is required.");
        if (request.UnitPrice <= 0) throw new ArgumentException("Unit price must be positive.");
        var amount = decimal.Round(request.UnitPrice, 3, MidpointRounding.AwayFromZero);
        var price = await db.ProductPrices.SingleOrDefaultAsync(x => x.ProductId == request.ProductId, ct);
        if (price is null) db.ProductPrices.Add(new ProductPriceEntity { Id = Guid.NewGuid(), ProductId = request.ProductId, UnitPrice = amount });
        else price.UnitPrice = amount;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeletePrice(Guid productId, CancellationToken ct)
    {
        var price = await db.ProductPrices.SingleOrDefaultAsync(x => x.ProductId == productId, ct);
        if (price is null) return false;
        db.ProductPrices.Remove(price);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<Guid> CreatePromotion(CreatePromotionRequest request, CancellationToken ct)
    {
        if (request.ProductId == Guid.Empty) throw new ArgumentException("Product ID is required.");
        if (request.DiscountPercent is < 0 or > 100) throw new ArgumentException("Discount must be between 0 and 100 percent.");
        if (request.ValidUntil <= request.ValidFrom) throw new ArgumentException("Promotion end must be after its start.");
        var id = Guid.NewGuid();
        db.Promotions.Add(new PromotionEntity { Id = id, ProductId = request.ProductId, Type = "PERCENTAGE_DISCOUNT",
            DiscountPercent = request.DiscountPercent, ValidFrom = request.ValidFrom, ValidUntil = request.ValidUntil, Active = true });
        await db.SaveChangesAsync(ct);
        return id;
    }

    public async Task<IReadOnlyList<PromotionView>> ListPromotions(Guid? productId, CancellationToken ct)
    {
        return await db.Promotions.AsNoTracking().Where(x => productId == null || x.ProductId == productId)
            .OrderByDescending(x => x.ValidFrom).ThenBy(x => x.Id)
            .Select(x => new PromotionView(x.Id, x.ProductId, x.DiscountPercent, x.ValidFrom, x.ValidUntil, x.Active))
            .ToListAsync(ct);
    }

    public async Task<bool> DeactivatePromotion(Guid promotionId, CancellationToken ct)
    {
        var promotion = await db.Promotions.SingleOrDefaultAsync(x => x.Id == promotionId, ct);
        if (promotion is null) return false;
        promotion.Active = false;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static PriceView ToPrice(Guid productId, decimal amount) =>
        new(productId, $"{amount.ToString("0.000", CultureInfo.InvariantCulture)} TND");

}

