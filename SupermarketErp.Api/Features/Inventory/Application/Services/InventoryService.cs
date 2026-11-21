using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Inventory;

/// <summary>Inventory use cases for the back office: families, the stock overview, and receiving stock by hand.</summary>
public sealed class InventoryService(IInventoryRepository repository, IUnitOfWork unitOfWork, IInventoryStock stock) : IInventoryService
{
    public async Task<IReadOnlyList<FamilyView>> ListFamilies(CancellationToken ct) =>
        (await repository.ListFamilies(ct)).Select(x => new FamilyView(x.Id, x.Name, x.ImageUrl)).ToList();

    public async Task<Guid> CreateFamily(CreateFamilyRequest request, CancellationToken ct)
    {
        var family = ProductFamilyEntity.Create(request.Name, request.ImageUrl);
        await unitOfWork.InTransaction(async () =>
        {
            if (await repository.FamilyNameTaken(family.Name, ct))
                throw new ConflictException("A product family with this name already exists.");
            repository.Add(family);
        }, ct);
        return family.Id;
    }

    public Task<IReadOnlyList<StockSummaryView>> ListStock(CancellationToken ct) => repository.ListStock(ct);

    public Task<StockLevelView?> GetStock(Guid productId, CancellationToken ct) => repository.GetStock(productId, ct);

    /// <summary>Receives a lot that did not come from a purchase order (for example a first stock count).</summary>
    public Task ReceiveStock(ReceiveStockRequest request, CancellationToken ct) =>
        unitOfWork.InTransaction(() => stock.ReceiveLot(new LotReceipt(request.ProductId, request.LotNumber, request.QuantityValue,
            request.UnitType ?? "", request.ExpiryDate, request.ReceivedDate, request.SupplierReference, request.UnitCost ?? 0m), ct), ct);
}
