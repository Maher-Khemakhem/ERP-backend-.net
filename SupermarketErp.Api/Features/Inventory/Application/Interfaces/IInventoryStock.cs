namespace SupermarketErp.Api.Features.Inventory;

/// <summary>A product as other modules may see it. Other modules never read the product tables themselves.</summary>
public sealed record ProductInfo(
    Guid Id,
    string Sku,
    string Name,
    Guid? FamilyId,
    string? FamilyName,
    string? FamilyImageUrl,
    string? ImageUrl,
    string UnitType,
    bool Perishable,
    int ReorderPoint,
    decimal VatRate,
    bool OnlineVisible);

/// <summary>Stock arriving as a new lot.</summary>
public sealed record LotReceipt(
    Guid ProductId,
    string? LotNumber,
    decimal Quantity,
    string UnitType,
    DateOnly? ExpiryDate,
    DateOnly? ReceivedDate,
    string? SupplierReference,
    decimal UnitCost);

/// <summary>
/// The Inventory module's public contract for the other modules (POS, Ecommerce, Procurement, Pricing).
/// Stock, lots and products belong to Inventory: nobody else touches those tables, so the stock rules
/// (first-expired-first-out, reservations, reorder-point alerts, unit validation) exist in exactly one place.
///
/// Transactions: the mutating operations take part in the CALLER's transaction. They never begin, commit or
/// roll one back, so a sale (POS) or a receipt (Procurement) stays atomic with its stock movement. They flush
/// their own changes (so later reads in the same transaction see them) but the caller decides whether to commit.
///
/// Errors: <see cref="ArgumentException"/> for invalid input (unknown product, unit mismatch, bad quantity);
/// <see cref="InvalidOperationException"/> for business conflicts (insufficient stock).
/// </summary>
public interface IInventoryStock
{
    // ---- products (read) -------------------------------------------------------------------

    Task<ProductInfo?> FindProduct(Guid productId, CancellationToken ct);

    /// <summary>Case-insensitive lookup by SKU (the phone scanner scans SKUs).</summary>
    Task<ProductInfo?> FindProductBySku(string sku, CancellationToken ct);

    Task<IReadOnlyDictionary<Guid, ProductInfo>> FindProducts(IReadOnlyCollection<Guid> productIds, CancellationToken ct);

    /// <summary>Products visible in the online store, optionally filtered by family and by name/SKU text.</summary>
    Task<IReadOnlyList<ProductInfo>> SearchOnlineProducts(string? search, Guid? familyId, CancellationToken ct);

    /// <summary>On hand minus reserved, per product. Products that have never had stock are absent.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> AvailableQuantities(IReadOnlyCollection<Guid> productIds, CancellationToken ct);

    // ---- stock movements (take part in the caller's transaction) ---------------------------

    /// <summary>Adds a lot. Validates the unit against the product and requires an expiry date for perishables.</summary>
    Task ReceiveLot(LotReceipt receipt, CancellationToken ct);

    /// <summary>Removes stock first-expired-first-out and returns the cost of goods sold.</summary>
    Task<decimal> Sell(Guid productId, decimal quantity, string unitType, CancellationToken ct);

    /// <summary>Sets stock aside for an online order without removing it.</summary>
    Task Reserve(Guid productId, decimal quantity, string unitType, CancellationToken ct);

    /// <summary>Puts reserved stock back on the shelf (order cancelled or expired).</summary>
    Task Release(Guid productId, decimal quantity, string unitType, CancellationToken ct);

    /// <summary>Turns reserved stock into a sale and returns the cost of goods sold.</summary>
    Task<decimal> ConsumeReserved(Guid productId, decimal quantity, string unitType, CancellationToken ct);
}
