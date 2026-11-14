namespace SupermarketErp.Api.Features.Pricing;

public sealed record SetPriceRequest(Guid ProductId, decimal UnitPrice);
public sealed record PriceView(Guid ProductId, string UnitPrice);
public sealed record CreatePromotionRequest(
    Guid ProductId,
    decimal DiscountPercent,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil);
public sealed record PromotionView(
    Guid Id,
    Guid ProductId,
    decimal DiscountPercent,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    bool Active);
