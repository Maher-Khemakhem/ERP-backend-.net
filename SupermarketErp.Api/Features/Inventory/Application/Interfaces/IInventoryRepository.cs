namespace SupermarketErp.Api.Features.Inventory;

/// <summary>Persistence for product families, plus the read models behind the stock overview screens.</summary>
public interface IInventoryRepository
{
    Task<IReadOnlyList<ProductFamilyEntity>> ListFamilies(CancellationToken ct);
    Task<bool> FamilyNameTaken(string name, CancellationToken ct);
    void Add(ProductFamilyEntity family);

    Task<IReadOnlyList<StockSummaryView>> ListStock(CancellationToken ct);
    Task<StockLevelView?> GetStock(Guid productId, CancellationToken ct);
}
