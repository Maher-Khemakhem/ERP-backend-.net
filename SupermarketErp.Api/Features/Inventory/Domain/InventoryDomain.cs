namespace SupermarketErp.Api.Features.Inventory;

/// <summary>A group of products (dairy, bakery...) shown together in the shop and the online store.</summary>
public sealed class ProductFamilyEntity
{
    private ProductFamilyEntity() { } // EF Core

    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public string? ImageUrl { get; private set; }

    public static ProductFamilyEntity Create(string? name, string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainValidationException("Family name is required.");
        if (name.Trim().Length > 160) throw new DomainValidationException("Family name cannot exceed 160 characters.");
        if (imageUrl?.Length > 500) throw new DomainValidationException("Image URL cannot exceed 500 characters.");
        return new ProductFamilyEntity { Id = Guid.NewGuid(), Name = name.Trim(), ImageUrl = Blank(imageUrl) };
    }

    internal static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A product in the catalog. Its stock lives in the stock level; its price in Pricing.</summary>
public sealed class ProductEntity
{
    public const decimal DefaultVatRate = 19m;

    private ProductEntity() { } // EF Core

    public Guid Id { get; private set; }
    public string Sku { get; private set; } = "";
    public string Name { get; private set; } = "";
    public Guid? FamilyId { get; private set; }
    public ProductFamilyEntity? Family { get; private set; }
    public string? ImageUrl { get; private set; }
    /// <summary>EACH (counted) or WEIGHT (kilograms, three decimals).</summary>
    public string UnitType { get; private set; } = "";
    public bool Perishable { get; private set; }
    public int? ReorderPoint { get; private set; }
    public decimal? VatRate { get; private set; }
    public bool OnlineVisible { get; private set; }

    public static ProductEntity Create(string? sku, string? name, Guid? familyId, string? imageUrl, string? unitType,
        bool perishable, int reorderPoint, decimal? vatRate)
    {
        if (string.IsNullOrWhiteSpace(sku)) throw new DomainValidationException("SKU is required.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainValidationException("Name is required.");
        if (imageUrl?.Length > 500) throw new DomainValidationException("Image URL cannot exceed 500 characters.");
        var unit = unitType?.Trim().ToUpperInvariant();
        if (unit is not ("EACH" or "WEIGHT")) throw new DomainValidationException("Unit type must be EACH or WEIGHT.");
        if (reorderPoint < 0) throw new DomainValidationException("Reorder point cannot be negative.");
        if (vatRate is < 0 or > 100) throw new DomainValidationException("VAT rate must be between 0 and 100.");

        return new ProductEntity
        {
            Id = Guid.NewGuid(), Sku = sku.Trim(), Name = name.Trim(), FamilyId = familyId,
            ImageUrl = ProductFamilyEntity.Blank(imageUrl), UnitType = unit, Perishable = perishable,
            ReorderPoint = reorderPoint, VatRate = vatRate ?? DefaultVatRate, OnlineVisible = true
        };
    }
}
