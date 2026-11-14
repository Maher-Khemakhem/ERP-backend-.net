namespace SupermarketErp.Api.Features.Inventory;

public sealed record ReceiveStockRequest(
    Guid ProductId,
    string? LotNumber,
    decimal QuantityValue,
    string UnitType,
    DateOnly? ExpiryDate,
    DateOnly? ReceivedDate,
    string? SupplierReference,
    decimal? UnitCost);
public sealed record ReceiveStockResponse(string Status);
public sealed record LotView(
    string Id,
    string LotNumber,
    string ProductId,
    decimal Quantity,
    decimal ReservedQuantity,
    decimal AvailableQuantity,
    string? ExpiryDate,
    string? ReceivedAt,
    decimal UnitCost,
    string? SupplierReference);
public sealed record StockLevelView(
    Guid ProductId,
    string ProductName,
    decimal TotalQuantity,
    decimal ReservedQuantity,
    decimal AvailableQuantity,
    IReadOnlyList<LotView> Lots);
public sealed record StockSummaryView(
    Guid ProductId,
    string Sku,
    string Name,
    Guid? FamilyId,
    string? FamilyName,
    string? FamilyImageUrl,
    string? ImageUrl,
    string UnitType,
    bool Perishable,
    int ReorderPoint,
    decimal TotalQuantity,
    decimal ReservedQuantity,
    decimal AvailableQuantity,
    int LotCount,
    string? EarliestExpiryDate,
    string Status);
public sealed record CreateFamilyRequest(string Name, string? ImageUrl);
public sealed record FamilyView(Guid Id, string Name, string? ImageUrl);
