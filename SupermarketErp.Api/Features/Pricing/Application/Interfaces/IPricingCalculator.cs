namespace SupermarketErp.Api.Features.Pricing;

/// <summary>
/// The Pricing module's public contract. Prices and promotions belong to Pricing: other modules ask it
/// what something costs and never read the price or promotion tables themselves.
/// </summary>
public interface IPricingCalculator
{
    /// <summary>Gross/net/VAT for a quantity of one product, promotions applied (used at the till).</summary>
    Task<CalculatedPrice> CalculateLine(
        Guid productId,
        decimal quantity,
        string unitType,
        CancellationToken ct);

    /// <summary>
    /// The current net unit price per product with the first active promotion applied (used by the online store).
    /// Products that have no price are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, decimal>> CurrentUnitPrices(
        IReadOnlyCollection<Guid> productIds,
        CancellationToken ct);
}
