namespace SupermarketErp.Api.Features.Ecommerce;

public interface IStorefrontCatalog
{
    Task<IReadOnlyList<StorefrontProductView>> List(string? search, Guid? familyId, CancellationToken ct);
    Task<StorefrontProductView?> Get(Guid id, CancellationToken ct);
}

public interface IStorefrontOrderService
{
    Task<CartView> GetCart(Guid customer, CancellationToken ct);
    Task<CartView> AddCartItem(Guid customer, QuantityRequest request, CancellationToken ct);
    Task<CartView> ChangeCartItem(Guid customer, QuantityRequest request, CancellationToken ct);
    Task<CartView> RemoveCartItem(Guid customer, Guid product, CancellationToken ct);
    Task<OnlineOrderView> Checkout(Guid customer, CancellationToken ct);
    Task<OnlineOrderView> ConfirmDemoPayment(Guid customer, Guid orderId, CancellationToken ct);
    Task<IReadOnlyList<OnlineOrderView>> ListCustomerOrders(Guid customer, CancellationToken ct);
    Task<OnlineOrderView> GetCustomerOrder(Guid customer, Guid orderId, CancellationToken ct);
    Task<OnlineOrderView> CancelOrder(Guid customer, Guid orderId, CancellationToken ct);
    Task<IReadOnlyList<StaffOnlineOrderView>> ListStaffOrders(CancellationToken ct);
    Task<StaffOnlineOrderView> GetStaffOrder(Guid orderId, CancellationToken ct);
    Task<StaffOnlineOrderView> AdvanceStatus(Guid orderId, string status, CancellationToken ct);
    Task<StaffOnlineOrderView> CompletePickup(Guid orderId, string code, string staff, CancellationToken ct);
    Task<StaffOnlineOrderView> CancelStaffOrder(Guid orderId, CancellationToken ct);
    Task ExpireDueOrders(CancellationToken ct);
    Task<OnlineOrderView> ConfirmStripePayment(Guid orderId, string reference, CancellationToken ct);
}

public interface IStripeCheckoutService
{
    StripeConfigurationView Configuration();
    Task<StripeQuote> Quote(Guid customerId, Guid orderId, CancellationToken ct);
    Task<StripeCheckoutResult> Create(Guid customerId, Guid orderId, CancellationToken ct);
    Task<string> Reconcile(Guid customerId, Guid orderId, CancellationToken ct);
    Task Webhook(string payload, string? signature, CancellationToken ct);
}
