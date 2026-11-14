namespace SupermarketErp.Api.Features.Inventory;

public sealed class InventoryService(IInventoryRepository repository) : IInventoryService
{
    public Task<IReadOnlyList<FamilyView>> ListFamilies(CancellationToken ct) =>
        repository.ListFamilies(ct);

    public Task<Guid> CreateFamily(CreateFamilyRequest request, CancellationToken ct) =>
        repository.CreateFamily(request, ct);

    public Task<IReadOnlyList<StockSummaryView>> ListStock(CancellationToken ct) =>
        repository.ListStock(ct);

    public Task<StockLevelView?> GetStock(Guid productId, CancellationToken ct) =>
        repository.GetStock(productId, ct);

    public Task ReceiveStock(ReceiveStockRequest request, CancellationToken ct) =>
        repository.ReceiveStock(request, ct);
}
