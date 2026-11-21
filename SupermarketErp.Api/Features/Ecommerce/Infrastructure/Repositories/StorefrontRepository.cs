using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Ecommerce;

public sealed class StorefrontRepository(BusinessDbContext db) : IStorefrontRepository
{
    public Task<CartEntity?> FindCart(Guid customerId, CancellationToken ct) =>
        db.Carts.SingleOrDefaultAsync(x => x.CustomerId == customerId, ct);

    public async Task<IReadOnlyList<CartItemEntity>> ListCartItems(Guid cartId, CancellationToken ct) =>
        await db.CartItems.AsNoTracking().Where(x => x.CartId == cartId).ToListAsync(ct);

    public Task<CartItemEntity?> FindCartItem(Guid cartId, Guid productId, CancellationToken ct) =>
        db.CartItems.SingleOrDefaultAsync(x => x.CartId == cartId && x.ProductId == productId, ct);

    public void Add(CartEntity cart) => db.Carts.Add(cart);
    public void Add(CartItemEntity item) => db.CartItems.Add(item);
    public void Remove(CartItemEntity item) => db.CartItems.Remove(item);
    public void ClearCart(Guid cartId) => db.CartItems.RemoveRange(db.CartItems.Where(x => x.CartId == cartId));

    public void Add(OnlineOrderEntity order) => db.OnlineOrders.Add(order);

    public Task<OnlineOrderEntity?> FindOrder(Guid id, CancellationToken ct) =>
        db.OnlineOrders.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, ct);

    public async Task<IReadOnlyList<OnlineOrderEntity>> ListOrders(Guid? customerId, Guid? orderId, CancellationToken ct)
    {
        var query = db.OnlineOrders.AsNoTracking().Include(x => x.Lines).AsQueryable();
        if (customerId is not null) query = query.Where(x => x.CustomerId == customerId);
        if (orderId is not null) query = query.Where(x => x.Id == orderId);
        return await query.OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<OnlineOrderEntity>> ListOverdueOrders(DateTimeOffset now, int limit, CancellationToken ct) =>
        await db.OnlineOrders.Include(x => x.Lines)
            .Where(x => x.Status == OnlineOrderStatus.PendingPayment && x.ExpiresAt <= now)
            .OrderBy(x => x.ExpiresAt).Take(limit).ToListAsync(ct);

    public Task<bool> HasStripeCheckout(Guid orderId, CancellationToken ct) =>
        db.StripeCheckouts.AnyAsync(x => x.OrderId == orderId, ct);

    public Task<bool> HasActiveStripeCheckout(Guid orderId, CancellationToken ct) =>
        db.StripeCheckouts.AnyAsync(x => x.OrderId == orderId && (x.Status == "CREATING" || x.Status == "PENDING"), ct);

    public async Task<IReadOnlyDictionary<Guid, string>> StripeStatuses(IReadOnlyCollection<Guid> orderIds, CancellationToken ct)
    {
        var ids = orderIds.ToArray();
        return await db.StripeCheckouts.AsNoTracking().Where(x => ids.Contains(x.OrderId)).ToDictionaryAsync(x => x.OrderId, x => x.Status, ct);
    }
}
