using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Procurement;

public sealed class ProcurementRepository(BusinessDbContext db) : IProcurementRepository
{
    public async Task<IReadOnlyList<SupplierEntity>> ListSuppliers(CancellationToken ct) =>
        await db.Suppliers.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);

    public Task<SupplierEntity?> FindSupplier(Guid id, CancellationToken ct) =>
        db.Suppliers.SingleOrDefaultAsync(x => x.Id == id, ct);

    public Task<bool> SupplierExists(Guid id, CancellationToken ct) =>
        db.Suppliers.AnyAsync(x => x.Id == id, ct);

    public async Task<IReadOnlyDictionary<Guid, string>> SupplierNames(CancellationToken ct) =>
        await db.Suppliers.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);

    public void Add(SupplierEntity supplier) => db.Suppliers.Add(supplier);
    public void Remove(SupplierEntity supplier) => db.Suppliers.Remove(supplier);

    public async Task<IReadOnlyList<PurchaseOrderEntity>> ListOrders(Guid? id, CancellationToken ct) =>
        await db.PurchaseOrders.AsNoTracking().Include(x => x.Lines)
            .Where(x => id == null || x.Id == id).OrderByDescending(x => x.PlacedAt).ToListAsync(ct);

    public Task<PurchaseOrderEntity?> FindOrder(Guid id, CancellationToken ct) =>
        db.PurchaseOrders.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, ct);

    public void Add(PurchaseOrderEntity order) => db.PurchaseOrders.Add(order);

    public async Task<IReadOnlyList<LowStockAlertEntity>> ListAlerts(CancellationToken ct) =>
        await db.LowStockAlerts.AsNoTracking().OrderByDescending(x => x.RaisedAt).ToListAsync(ct);

    public Task<LowStockAlertEntity?> FindAlert(Guid id, CancellationToken ct) =>
        db.LowStockAlerts.SingleOrDefaultAsync(x => x.Id == id, ct);
}
