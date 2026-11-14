namespace SupermarketErp.Api.Features.Inventory;

public interface IInventoryService
{
    Task<IReadOnlyList<FamilyView>> ListFamilies(CancellationToken ct);
    Task<Guid> CreateFamily(CreateFamilyRequest request, CancellationToken ct);
    Task<IReadOnlyList<StockSummaryView>> ListStock(CancellationToken ct);
    Task<StockLevelView?> GetStock(Guid productId, CancellationToken ct);
    Task ReceiveStock(ReceiveStockRequest request, CancellationToken ct);
}
