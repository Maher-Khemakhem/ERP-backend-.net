namespace SupermarketErp.Api.Features.Inventory;

public sealed record CreateProductRequest(
    string Sku,
    string Name,
    Guid? FamilyId,
    string? ImageUrl,
    string UnitType,
    bool Perishable,
    int ReorderPoint,
    decimal? VatRate);

public sealed record ProductView(
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
    decimal VatRate);
