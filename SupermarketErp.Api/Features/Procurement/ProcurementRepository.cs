using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Procurement;

public sealed class ProcurementRepository(BusinessDbContext db)
{
    public async Task<IReadOnlyList<SupplierView>> ListSuppliers(CancellationToken ct) =>
        await db.Suppliers.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new SupplierView(x.Id, x.Name, x.ContactEmail, x.ContactPhone, x.Address)).ToListAsync(ct);
    public async Task<SupplierView?> GetSupplier(Guid id, CancellationToken ct) =>
        await db.Suppliers.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new SupplierView(x.Id, x.Name, x.ContactEmail, x.ContactPhone, x.Address)).SingleOrDefaultAsync(ct);

    public async Task<Guid> CreateSupplier(SupplierRequest request, CancellationToken ct)
    {
        ValidateSupplier(request); var id = Guid.NewGuid();
        db.Suppliers.Add(new SupplierEntity { Id = id, Name = request.Name.Trim(), ContactEmail = request.ContactEmail, ContactPhone = request.Phone, Address = request.Address });
        await db.SaveChangesAsync(ct); return id;
    }
    public async Task<bool> UpdateSupplier(Guid id, SupplierRequest request, CancellationToken ct)
    {
        ValidateSupplier(request); var s = await db.Suppliers.FindAsync([id], ct); if (s is null) return false;
        s.Name = request.Name.Trim(); s.ContactEmail = request.ContactEmail; s.ContactPhone = request.Phone; s.Address = request.Address;
        await db.SaveChangesAsync(ct); return true;
    }
    public async Task<bool> DeleteSupplier(Guid id, CancellationToken ct)
    {
        var s = await db.Suppliers.FindAsync([id], ct); if (s is null) return false;
        db.Suppliers.Remove(s); await db.SaveChangesAsync(ct); return true;
    }

    public async Task<Guid> CreateOrder(CreateOrderRequest request, CancellationToken ct)
    {
        if (request.SupplierId == Guid.Empty || !await db.Suppliers.AnyAsync(x => x.Id == request.SupplierId, ct)) throw new ArgumentException("Supplier not found.");
        if (request.Lines is null || request.Lines.Count == 0) throw new ArgumentException("At least one order line is required.");
        var order = new PurchaseOrderEntity { Id = Guid.NewGuid(), SupplierId = request.SupplierId, Status = "DRAFT", PlacedAt = DateTimeOffset.UtcNow };
        foreach (var line in request.Lines)
        {
            ValidateQuantity(line.QuantityValue, line.UnitType); if (line.UnitCost < 0) throw new ArgumentException("Unit cost cannot be negative.");
            var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == line.ProductId, ct);
            if (product is null || !product.UnitType.Equals(line.UnitType, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"Product or unit type is invalid: {line.ProductId}");
            order.Lines.Add(new PurchaseOrderLineEntity { Id = Guid.NewGuid(), ProductId = line.ProductId, Quantity = line.QuantityValue, UnitType = Unit(line.UnitType), UnitCost = line.UnitCost });
        }
        db.PurchaseOrders.Add(order); await db.SaveChangesAsync(ct); return order.Id;
    }
    public async Task PlaceOrder(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var order = await db.PurchaseOrders.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ArgumentException($"Purchase order not found: {id}");
        if (order.Status != "DRAFT") throw new InvalidOperationException($"Purchase order {id}: expected DRAFT, current status {order.Status}");
        if (order.Lines.Count == 0) throw new InvalidOperationException("Impossible d'envoyer une commande sans lignes");
        order.Status = "PLACED"; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task CancelOrder(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var order = await db.PurchaseOrders.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ArgumentException($"Purchase order not found: {id}");
        if (order.Status is not ("DRAFT" or "PLACED")) throw new InvalidOperationException($"Commande {id} : seule une commande DRAFT ou PLACED peut être annulée");
        order.Status = "CANCELLED"; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task ReceiveOrder(Guid id, ReceiveOrderRequest request, CancellationToken ct)
    {
        if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Select(x => x.PurchaseOrderLineId).Distinct().Count() != request.Lines.Count)
            throw new ArgumentException("A receipt must contain every order line exactly once.");
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var order = await db.PurchaseOrders.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ArgumentException($"Purchase order not found: {id}");
        if (order.Status != "PLACED") throw new InvalidOperationException("Only a placed purchase order can be received.");
        if (order.Lines.Count != request.Lines.Count || request.Lines.Any(x => !order.Lines.Any(l => l.Id == x.PurchaseOrderLineId)))
            throw new ArgumentException("A receipt must include every order line exactly once.");
        foreach (var receipt in request.Lines)
        {
            ValidateQuantity(receipt.QuantityValue, receipt.UnitType); var line = order.Lines.Single(x => x.Id == receipt.PurchaseOrderLineId);
            if (line.UnitType != Unit(receipt.UnitType) || line.Quantity != receipt.QuantityValue) throw new ArgumentException("Received quantity and unit must match the purchase order.");
            var product = await db.Products.SingleAsync(x => x.Id == line.ProductId, ct);
            if (product.Perishable && receipt.ExpiryDate is null) throw new ArgumentException("Perishable products require an expiry date.");
            var stock = await db.StockLevels.SingleOrDefaultAsync(x => x.ProductId == line.ProductId, ct);
            if (stock is null) { stock = new StockLevelEntity { Id = Guid.NewGuid(), ProductId = line.ProductId, Version = 0 }; db.StockLevels.Add(stock); }
            else stock.Version++;
            var lot = new LotEntity { Id = Guid.NewGuid(), LotNumber = string.IsNullOrWhiteSpace(receipt.LotNumber) ? $"LOT-{Guid.NewGuid():N}"[..12].ToUpperInvariant() : receipt.LotNumber.Trim(), ProductId = line.ProductId, ExpiryDate = receipt.ExpiryDate, ReceivedDate = receipt.ReceivedDate ?? DateOnly.FromDateTime(DateTime.Today), SupplierReference = receipt.SupplierReference, UnitCost = line.UnitCost };
            db.Lots.Add(lot); db.LotEntries.Add(new LotEntryEntity { Id = Guid.NewGuid(), StockLevelId = stock.Id, LotId = lot.Id, OnHandValue = receipt.QuantityValue, OnHandUnitType = line.UnitType, ReservedValue = 0, ReservedUnitType = line.UnitType });
        }
        order.Status = "RECEIVED"; db.DotnetOutbox.Add(new DotnetOutboxEntity { Id = Guid.NewGuid(), EventType = "GoodsReceived", Payload = JsonSerializer.Serialize(new GoodsReceivedEvent(id, order.SupplierId, order.Lines.Sum(x => x.Quantity * x.UnitCost))), OccurredAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task<OrderView?> GetOrder(Guid id, CancellationToken ct) => (await ReadOrders(id, ct)).FirstOrDefault();
    public Task<IReadOnlyList<OrderView>> ListOrders(CancellationToken ct) => ReadOrders(null, ct);
    private async Task<IReadOnlyList<OrderView>> ReadOrders(Guid? id, CancellationToken ct)
    {
        var orders = await db.PurchaseOrders.AsNoTracking().Include(x => x.Lines).Where(x => id == null || x.Id == id).OrderByDescending(x => x.PlacedAt).ToListAsync(ct);
        var suppliers = await db.Suppliers.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        return orders.Select(o => new OrderView(o.Id, o.SupplierId, suppliers.GetValueOrDefault(o.SupplierId), o.Status,
            o.Lines.OrderBy(x => x.Id).Select(x => new OrderLineView(x.Id, x.ProductId, x.Quantity.ToString(), x.UnitType == 0 ? "EACH" : "WEIGHT", x.UnitCost.ToString())).ToArray(),
            o.Lines.Sum(x => x.Quantity * x.UnitCost).ToString(), null, JavaInstantFormat.Format(o.PlacedAt), null)).ToArray();
    }
    public async Task<IReadOnlyList<AlertView>> ListAlerts(CancellationToken ct)
    {
        var alerts = await db.LowStockAlerts.AsNoTracking().OrderByDescending(x => x.RaisedAt).ToListAsync(ct);
        var products = await db.Products.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var stocks = await db.StockLevels.AsNoTracking().ToDictionaryAsync(x => x.ProductId, ct);
        var entries = await db.LotEntries.AsNoTracking().ToListAsync(ct);
        return alerts.Select(a => { products.TryGetValue(a.ProductId, out var p); stocks.TryGetValue(a.ProductId, out var s); var current = entries.Where(e => e.StockLevelId == s?.Id).Sum(e => (e.OnHandValue ?? 0) - (e.ReservedValue ?? 0)); return new AlertView(a.Id, a.ProductId, p?.Name ?? "Unknown product", (double)current, p?.ReorderPoint ?? 0, JavaInstantFormat.Format(a.RaisedAt), a.Acknowledged); }).ToArray();
    }
    public async Task AcknowledgeAlert(Guid id, CancellationToken ct) { var a = await db.LowStockAlerts.FindAsync([id], ct) ?? throw new ArgumentException($"Alert not found: {id}"); a.Acknowledged = true; await db.SaveChangesAsync(ct); }

    private static short Unit(string unit) => unit.Equals("EACH", StringComparison.OrdinalIgnoreCase) ? (short)0 : (short)1;
    private static void ValidateSupplier(SupplierRequest r) { if (string.IsNullOrWhiteSpace(r.Name)) throw new ArgumentException("Supplier name is required."); if (r.Name.Trim().Length > 255) throw new ArgumentException("Supplier name is too long."); if (r.Address?.Length > 500) throw new ArgumentException("Address cannot exceed 500 characters."); }
    private static void ValidateQuantity(decimal value, string unit) { if (value <= 0) throw new ArgumentException("Quantity must be positive."); if (unit is not ("EACH" or "each" or "WEIGHT" or "weight")) throw new ArgumentException("Unit type must be EACH or WEIGHT."); if (unit.Equals("EACH", StringComparison.OrdinalIgnoreCase) && value != decimal.Truncate(value)) throw new ArgumentException("EACH quantity must be a whole number."); if (unit.Equals("WEIGHT", StringComparison.OrdinalIgnoreCase) && decimal.Round(value, 3) != value) throw new ArgumentException("WEIGHT quantities support at most three decimal places."); }
}
