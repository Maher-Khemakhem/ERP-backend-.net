namespace SupermarketErp.Api.Features.Inventory;

/// <summary>Persistence for products: loads and stores entities and nothing else.</summary>
public interface IProductRepository
{
    Task<IReadOnlyList<ProductEntity>> List(CancellationToken ct);
    Task<ProductEntity?> Find(Guid productId, CancellationToken ct);
    Task<bool> FamilyExists(Guid familyId, CancellationToken ct);
    void Add(ProductEntity product);
    void Remove(ProductEntity product);
}
