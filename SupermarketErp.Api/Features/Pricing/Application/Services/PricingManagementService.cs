using System.Globalization;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Pricing;

public sealed class PricingManagementService(IPricingRepository repository)
    : IPricingManagementService
{
    public async Task<IReadOnlyList<PriceView>> ListPrices(CancellationToken ct) =>
        (await repository.ListPrices(ct))
        .Select(x => ToPrice(x.ProductId, x.UnitPrice ?? 0))
        .ToList();

    public async Task<PriceView?> GetPrice(Guid productId, CancellationToken ct)
    {
        var price = await repository.GetPrice(productId, ct);
        return price is null ? null : ToPrice(price.ProductId, price.UnitPrice ?? 0);
    }

    public Task SetPrice(SetPriceRequest request, CancellationToken ct)
    {
        if (request.ProductId == Guid.Empty)
            throw new ArgumentException("Product ID is required.");
        if (request.UnitPrice <= 0)
            throw new ArgumentException("Unit price must be positive.");

        var amount = decimal.Round(request.UnitPrice, 3, MidpointRounding.AwayFromZero);
        return repository.UpsertPrice(request.ProductId, amount, ct);
    }

    public Task<bool> DeletePrice(Guid productId, CancellationToken ct) =>
        repository.DeletePrice(productId, ct);

    public async Task<IReadOnlyList<PromotionView>> ListPromotions(
        Guid? productId,
        CancellationToken ct) =>
        (await repository.ListPromotions(productId, ct))
        .Select(x => new PromotionView(
            x.Id,
            x.ProductId,
            x.DiscountPercent,
            x.ValidFrom,
            x.ValidUntil,
            x.Active))
        .ToList();

    public Task<Guid> CreatePromotion(CreatePromotionRequest request, CancellationToken ct)
    {
        if (request.ProductId == Guid.Empty)
            throw new ArgumentException("Product ID is required.");
        if (request.DiscountPercent is < 0 or > 100)
            throw new ArgumentException("Discount must be between 0 and 100 percent.");
        if (request.ValidUntil <= request.ValidFrom)
            throw new ArgumentException("Promotion end must be after its start.");

        return repository.AddPromotion(new PromotionEntity
        {
            Id = Guid.NewGuid(),
            ProductId = request.ProductId,
            Type = "PERCENTAGE_DISCOUNT",
            DiscountPercent = request.DiscountPercent,
            ValidFrom = request.ValidFrom,
            ValidUntil = request.ValidUntil,
            Active = true
        }, ct);
    }

    public Task<bool> DeactivatePromotion(Guid promotionId, CancellationToken ct) =>
        repository.DeactivatePromotion(promotionId, ct);

    private static PriceView ToPrice(Guid productId, decimal amount) =>
        new(productId, $"{amount.ToString("0.000", CultureInfo.InvariantCulture)} TND");
}
