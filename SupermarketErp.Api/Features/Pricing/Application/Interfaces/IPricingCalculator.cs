namespace SupermarketErp.Api.Features.Pricing;

public interface IPricingCalculator
{
    Task<CalculatedPrice> CalculateLine(
        Guid productId,
        decimal quantity,
        string unitType,
        CancellationToken ct);
}
