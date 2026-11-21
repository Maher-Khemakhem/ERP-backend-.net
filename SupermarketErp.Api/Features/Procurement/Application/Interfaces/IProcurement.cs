namespace SupermarketErp.Api.Features.Procurement;

/// <summary>Procurement use cases, called by the HTTP endpoints.</summary>
public interface IProcurementService
{
    Task<IReadOnlyList<SupplierView>> ListSuppliers(CancellationToken ct);
    Task<SupplierView?> GetSupplier(Guid id, CancellationToken ct);
    Task<Guid> CreateSupplier(SupplierRequest request, CancellationToken ct);
    Task<bool> UpdateSupplier(Guid id, SupplierRequest request, CancellationToken ct);
    Task<bool> DeleteSupplier(Guid id, CancellationToken ct);

    Task<Guid> CreateOrder(CreateOrderRequest request, CancellationToken ct);
    Task PlaceOrder(Guid id, CancellationToken ct);
    Task CancelOrder(Guid id, CancellationToken ct);
    Task ReceiveOrder(Guid id, ReceiveOrderRequest request, CancellationToken ct);
    Task<OrderView?> GetOrder(Guid id, CancellationToken ct);
    Task<IReadOnlyList<OrderView>> ListOrders(CancellationToken ct);

    Task<IReadOnlyList<AlertView>> ListAlerts(CancellationToken ct);
    Task AcknowledgeAlert(Guid id, CancellationToken ct);
}

/// <summary>
/// Persistence for the procurement aggregates: loads and stores entities and nothing else. Business rules live
/// in the domain entities and <see cref="ProcurementService"/>; saving and transactions in the unit of work.
/// </summary>
public interface IProcurementRepository
{
    Task<IReadOnlyList<SupplierEntity>> ListSuppliers(CancellationToken ct);
    Task<SupplierEntity?> FindSupplier(Guid id, CancellationToken ct);
    Task<bool> SupplierExists(Guid id, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, string>> SupplierNames(CancellationToken ct);
    void Add(SupplierEntity supplier);
    void Remove(SupplierEntity supplier);

    Task<IReadOnlyList<PurchaseOrderEntity>> ListOrders(Guid? id, CancellationToken ct);
    /// <summary>The order with its lines, or null.</summary>
    Task<PurchaseOrderEntity?> FindOrder(Guid id, CancellationToken ct);
    void Add(PurchaseOrderEntity order);

    Task<IReadOnlyList<LowStockAlertEntity>> ListAlerts(CancellationToken ct);
    Task<LowStockAlertEntity?> FindAlert(Guid id, CancellationToken ct);
    Task<bool> HasOpenAlert(Guid productId, CancellationToken ct);
    void Add(LowStockAlertEntity alert);
}
