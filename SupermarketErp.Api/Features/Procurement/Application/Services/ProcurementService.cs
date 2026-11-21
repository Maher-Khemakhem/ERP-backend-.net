using System.Globalization;
using SupermarketErp.Api.Features.Inventory;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Procurement;

/// <summary>
/// Procurement use cases: suppliers, purchase orders (draft -> placed -> received) and low-stock alerts.
/// It orchestrates; the entities enforce the rules. Receiving an order puts the goods into stock through
/// Inventory's contract, in the same unit of work, and publishes <c>GoodsReceived</c> for Finance.
/// </summary>
public sealed class ProcurementService(
    IProcurementRepository repository,
    IUnitOfWork unitOfWork,
    IInventoryStock inventory,
    IEventOutbox outbox) : IProcurementService
{
    // ---- suppliers -------------------------------------------------------------------------

    public async Task<IReadOnlyList<SupplierView>> ListSuppliers(CancellationToken ct) =>
        (await repository.ListSuppliers(ct)).Select(Supplier).ToList();

    public async Task<SupplierView?> GetSupplier(Guid id, CancellationToken ct) =>
        await repository.FindSupplier(id, ct) is { } supplier ? Supplier(supplier) : null;

    public async Task<Guid> CreateSupplier(SupplierRequest request, CancellationToken ct)
    {
        var supplier = SupplierEntity.Create(request.Name, request.ContactEmail, request.Phone, request.Address);
        await unitOfWork.InTransaction(() => { repository.Add(supplier); return Task.CompletedTask; }, ct);
        return supplier.Id;
    }

    public Task<bool> UpdateSupplier(Guid id, SupplierRequest request, CancellationToken ct) =>
        unitOfWork.InTransaction(async () =>
        {
            var supplier = await repository.FindSupplier(id, ct);
            if (supplier is null) return false;
            supplier.Update(request.Name, request.ContactEmail, request.Phone, request.Address);
            return true;
        }, ct);

    public Task<bool> DeleteSupplier(Guid id, CancellationToken ct) =>
        unitOfWork.InTransaction(async () =>
        {
            var supplier = await repository.FindSupplier(id, ct);
            if (supplier is null) return false;
            repository.Remove(supplier);
            return true;
        }, ct);

    // ---- purchase orders -------------------------------------------------------------------

    public async Task<Guid> CreateOrder(CreateOrderRequest request, CancellationToken ct)
    {
        if (request.SupplierId == Guid.Empty || !await repository.SupplierExists(request.SupplierId, ct))
            throw new DomainValidationException("Supplier not found.");
        if (request.Lines is null || request.Lines.Count == 0)
            throw new DomainValidationException("At least one order line is required.");

        var order = PurchaseOrderEntity.Draft(request.SupplierId, DateTimeOffset.UtcNow);
        foreach (var line in request.Lines)
        {
            PurchaseOrderLineEntity.ValidateQuantity(line.QuantityValue, line.UnitType);
            if (line.UnitCost < 0) throw new DomainValidationException("Unit cost cannot be negative.");
            var product = await inventory.FindProduct(line.ProductId, ct);
            if (product is null || !product.UnitType.Equals(line.UnitType, StringComparison.OrdinalIgnoreCase))
                throw new DomainValidationException($"Product or unit type is invalid: {line.ProductId}");
            order.AddLine(line.ProductId, line.QuantityValue, line.UnitType, line.UnitCost);
        }

        await unitOfWork.InTransaction(() => { repository.Add(order); return Task.CompletedTask; }, ct);
        return order.Id;
    }

    public Task PlaceOrder(Guid id, CancellationToken ct) =>
        unitOfWork.InTransaction(async () => (await RequireOrder(id, ct)).Place(), ct);

    public Task CancelOrder(Guid id, CancellationToken ct) =>
        unitOfWork.InTransaction(async () => (await RequireOrder(id, ct)).Cancel(), ct);

    public Task ReceiveOrder(Guid id, ReceiveOrderRequest request, CancellationToken ct)
    {
        var received = request.Lines?.Select(x => new ReceivedLine(x.PurchaseOrderLineId, x.QuantityValue, x.UnitType)).ToList();
        PurchaseOrderEntity.RequireWellFormedReceipt(received);

        return unitOfWork.InTransaction(async () =>
        {
            var order = await RequireOrder(id, ct);
            var lines = order.MatchReceipt(received!);

            for (var i = 0; i < lines.Count; i++)
            {
                var arrival = request.Lines![i];
                await inventory.ReceiveLot(new LotReceipt(lines[i].ProductId, arrival.LotNumber, arrival.QuantityValue, arrival.UnitType,
                    arrival.ExpiryDate, arrival.ReceivedDate, arrival.SupplierReference, lines[i].UnitCost), ct);
            }

            order.MarkReceived();
            outbox.Publish("GoodsReceived", new GoodsReceivedEvent(id, order.SupplierId, order.TotalCost));
        }, ct);
    }

    public async Task<OrderView?> GetOrder(Guid id, CancellationToken ct) =>
        (await ReadOrders(id, ct)).FirstOrDefault();

    public Task<IReadOnlyList<OrderView>> ListOrders(CancellationToken ct) => ReadOrders(null, ct);

    private async Task<IReadOnlyList<OrderView>> ReadOrders(Guid? id, CancellationToken ct)
    {
        var orders = await repository.ListOrders(id, ct);
        var suppliers = await repository.SupplierNames(ct);
        return orders.Select(order => new OrderView(
            order.Id, order.SupplierId, suppliers.GetValueOrDefault(order.SupplierId), order.Status,
            order.Lines.OrderBy(x => x.Id).Select(x => new OrderLineView(
                x.Id, x.ProductId, Text(x.Quantity), x.UnitName, Text(x.UnitCost))).ToArray(),
            Text(order.TotalCost), null, JavaInstantFormat.Format(order.PlacedAt), null)).ToArray();
    }

    // ---- low-stock alerts ------------------------------------------------------------------

    public async Task<IReadOnlyList<AlertView>> ListAlerts(CancellationToken ct)
    {
        var alerts = await repository.ListAlerts(ct);
        var productIds = alerts.Select(a => a.ProductId).Distinct().ToArray();
        var products = await inventory.FindProducts(productIds, ct);
        var available = await inventory.AvailableQuantities(productIds, ct);
        return alerts.Select(alert =>
        {
            products.TryGetValue(alert.ProductId, out var product);
            return new AlertView(alert.Id, alert.ProductId, product?.Name ?? "Unknown product",
                (double)available.GetValueOrDefault(alert.ProductId), product?.ReorderPoint ?? 0,
                JavaInstantFormat.Format(alert.RaisedAt), alert.Acknowledged);
        }).ToArray();
    }

    public Task AcknowledgeAlert(Guid id, CancellationToken ct) =>
        unitOfWork.InTransaction(async () =>
        {
            var alert = await repository.FindAlert(id, ct) ?? throw new DomainValidationException($"Alert not found: {id}");
            alert.Acknowledge();
        }, ct);

    // ---- helpers ---------------------------------------------------------------------------

    private async Task<PurchaseOrderEntity> RequireOrder(Guid id, CancellationToken ct) =>
        await repository.FindOrder(id, ct) ?? throw new DomainValidationException($"Purchase order not found: {id}");

    private static SupplierView Supplier(SupplierEntity x) => new(x.Id, x.Name, x.ContactEmail, x.ContactPhone, x.Address);

    /// <summary>Numbers in responses never depend on the server's regional settings.</summary>
    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
