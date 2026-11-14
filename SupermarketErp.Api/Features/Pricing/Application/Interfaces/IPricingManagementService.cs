namespace SupermarketErp.Api.Features.Pricing;

public interface IPricingManagementService
{
    Task<IReadOnlyList<PriceView>> ListPrices(CancellationToken ct);
    Task<PriceView?> GetPrice(Guid productId, CancellationToken ct);
    Task SetPrice(SetPriceRequest request, CancellationToken ct);
    Task<bool> DeletePrice(Guid productId, CancellationToken ct);
    Task<IReadOnlyList<PromotionView>> ListPromotions(Guid? productId, CancellationToken ct);
    Task<Guid> CreatePromotion(CreatePromotionRequest request, CancellationToken ct);
    Task<bool> DeactivatePromotion(Guid promotionId, CancellationToken ct);
}
