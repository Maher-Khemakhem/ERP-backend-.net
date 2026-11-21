namespace SupermarketErp.Api.Features.Ecommerce;

/// <summary>
/// Persistence for the storefront aggregates (carts and online orders): loads and stores entities and nothing
/// else. Rules live in the domain entities and <see cref="StorefrontOrderService"/>.
/// </summary>
public interface IStorefrontRepository
{
    // carts
    Task<CartEntity?> FindCart(Guid customerId, CancellationToken ct);
    Task<IReadOnlyList<CartItemEntity>> ListCartItems(Guid cartId, CancellationToken ct);
    Task<CartItemEntity?> FindCartItem(Guid cartId, Guid productId, CancellationToken ct);
    void Add(CartEntity cart);
    void Add(CartItemEntity item);
    void Remove(CartItemEntity item);
    void ClearCart(Guid cartId);

    // orders
    void Add(OnlineOrderEntity order);
    /// <summary>The order with its lines, or null.</summary>
    Task<OnlineOrderEntity?> FindOrder(Guid id, CancellationToken ct);
    Task<IReadOnlyList<OnlineOrderEntity>> ListOrders(Guid? customerId, Guid? orderId, CancellationToken ct);
    /// <summary>Orders still waiting for payment whose payment window has passed, oldest first.</summary>
    Task<IReadOnlyList<OnlineOrderEntity>> ListOverdueOrders(DateTimeOffset now, int limit, CancellationToken ct);

    // card payments (owned by the Stripe flow; only read here)
    Task<bool> HasStripeCheckout(Guid orderId, CancellationToken ct);
    Task<bool> HasActiveStripeCheckout(Guid orderId, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, string>> StripeStatuses(IReadOnlyCollection<Guid> orderIds, CancellationToken ct);
}
