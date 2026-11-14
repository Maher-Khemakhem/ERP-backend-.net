using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Pricing;

public interface IPricingRepository
{
    Task<IReadOnlyList<ProductPriceEntity>> ListPrices(CancellationToken ct);
    Task<ProductPriceEntity?> GetPrice(Guid productId, CancellationToken ct);
    Task<decimal?> GetActiveDiscount(Guid productId, DateTimeOffset at, CancellationToken ct);
    Task UpsertPrice(Guid productId, decimal amount, CancellationToken ct);
    Task<bool> DeletePrice(Guid productId, CancellationToken ct);
    Task<IReadOnlyList<PromotionEntity>> ListPromotions(Guid? productId, CancellationToken ct);
    Task<Guid> AddPromotion(PromotionEntity promotion, CancellationToken ct);
    Task<bool> DeactivatePromotion(Guid promotionId, CancellationToken ct);
}
