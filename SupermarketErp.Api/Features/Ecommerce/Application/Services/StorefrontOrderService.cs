using SupermarketErp.Api.Features.Auth;
using SupermarketErp.Api.Features.Inventory;
using SupermarketErp.Api.Features.Pricing;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Ecommerce;

/// <summary>
/// Online store use cases: cart, checkout, payment, cancellation, expiry and staff fulfilment. It orchestrates:
/// stock goes through Inventory's contract (reserve at checkout, sell at payment, release on cancel/expiry),
/// prices through Pricing's, and the order's own state rules live in <see cref="OnlineOrderEntity"/>.
/// Every change runs in one unit of work; views are read after it commits.
/// </summary>
public sealed class StorefrontOrderService(
    IStorefrontRepository repository,
    IUnitOfWork unitOfWork,
    IEventOutbox outbox,
    IConfiguration configuration,
    IInventoryStock inventory,
    IPricingCalculator pricing,
    IAuthService users) : IStorefrontOrderService
{
    private readonly int _paymentWindowMinutes = ReadPositive(configuration, "ECOMMERCE_PAYMENT_TIMEOUT_MINUTES", 15);

    // ---- cart ------------------------------------------------------------------------------

    public async Task<CartView> GetCart(Guid customer, CancellationToken ct)
    {
        var cartId = await unitOfWork.InTransaction(async () => (await GetOrCreateCart(customer, ct)).Id, ct);
        return await CartView(cartId, ct);
    }

    public async Task<CartView> AddCartItem(Guid customer, QuantityRequest request, CancellationToken ct)
    {
        CartItemEntity.ValidateQuantity(request.QuantityValue, request.UnitType);
        var cartId = await unitOfWork.InTransaction(async () =>
        {
            var cart = await GetOrCreateCart(customer, ct);
            var unit = await RequireOnlineProduct(request.ProductId, request.UnitType, ct);
            var item = await repository.FindCartItem(cart.Id, request.ProductId, ct);
            if (item is null) repository.Add(CartItemEntity.Create(cart.Id, request.ProductId, request.QuantityValue, unit));
            else item.Increase(request.QuantityValue, unit);
            cart.Touch(DateTimeOffset.UtcNow);
            return cart.Id;
        }, ct);
        return await CartView(cartId, ct);
    }

    public async Task<CartView> ChangeCartItem(Guid customer, QuantityRequest request, CancellationToken ct)
    {
        CartItemEntity.ValidateQuantity(request.QuantityValue, request.UnitType);
        var cartId = await unitOfWork.InTransaction(async () =>
        {
            var cart = await RequireCart(customer, ct);
            var unit = await RequireOnlineProduct(request.ProductId, request.UnitType, ct);
            var item = await repository.FindCartItem(cart.Id, request.ProductId, ct)
                ?? throw new ConflictException("Cart item not found.");
            item.SetQuantity(request.QuantityValue, unit);
            cart.Touch(DateTimeOffset.UtcNow);
            return cart.Id;
        }, ct);
        return await CartView(cartId, ct);
    }

    public async Task<CartView> RemoveCartItem(Guid customer, Guid product, CancellationToken ct)
    {
        var cartId = await unitOfWork.InTransaction(async () =>
        {
            var cart = await RequireCart(customer, ct);
            var item = await repository.FindCartItem(cart.Id, product, ct);
            if (item is not null) repository.Remove(item);
            cart.Touch(DateTimeOffset.UtcNow);
            return cart.Id;
        }, ct);
        return await CartView(cartId, ct);
    }

    // ---- checkout, payment, cancellation, expiry -------------------------------------------

    /// <summary>Turns the cart into an order awaiting payment and reserves its stock (all or nothing).</summary>
    public async Task<OnlineOrderView> Checkout(Guid customer, CancellationToken ct)
    {
        var orderId = await unitOfWork.InTransaction(async () =>
        {
            var cart = await GetOrCreateCart(customer, ct);
            var rows = await Rows(cart.Id, ct);
            if (rows.Count == 0) throw new ConflictException("Cannot check out an empty cart.");
            if (rows.Any(x => !x.Visible || !x.HasPrice))
                throw new ConflictException("The cart contains a product that is no longer available online.");

            var now = DateTimeOffset.UtcNow;
            var net = EcommerceViews.Round(rows.Sum(x => EcommerceViews.Round(x.Price * x.Quantity)));
            var vat = EcommerceViews.Round(rows.Sum(x => EcommerceViews.Round(EcommerceViews.Round(x.Price * x.Quantity) * x.VatRate / 100m)));
            var order = OnlineOrderEntity.Place(customer, net, vat, now, _paymentWindowMinutes);
            foreach (var row in rows)
            {
                var lineNet = EcommerceViews.Round(row.Price * row.Quantity);
                var lineVat = EcommerceViews.Round(lineNet * row.VatRate / 100m);
                await inventory.Reserve(row.ProductId, row.Quantity, row.Unit, ct);
                order.AddLine(row.ProductId, row.Name, row.Quantity, row.Unit, row.Price, lineNet + lineVat, lineVat);
            }

            repository.Add(order);
            repository.ClearCart(cart.Id);
            cart.Touch(now);
            return order.Id;
        }, ct);
        return await GetOrder(orderId, ct);
    }

    public Task<OnlineOrderView> ConfirmDemoPayment(Guid customer, Guid orderId, CancellationToken ct) =>
        Confirm(orderId, customer, $"MOCK-CARD-{orderId}", "CARD", honourPaymentWindow: true, ct);

    public Task<OnlineOrderView> ConfirmStripePayment(Guid orderId, string reference, CancellationToken ct) =>
        Confirm(orderId, null, reference, "STRIPE", honourPaymentWindow: false, ct);

    /// <summary>
    /// Pays an order: its reserved stock is sold and <c>OnlineSaleConfirmed</c> is published for Finance.
    /// Paying twice is harmless. A demo payment after the payment window gives the stock back instead.
    /// </summary>
    private async Task<OnlineOrderView> Confirm(Guid id, Guid? customer, string reference, string method, bool honourPaymentWindow, CancellationToken ct)
    {
        await unitOfWork.InTransaction(async () =>
        {
            var order = await RequireOrder(id, customer, "Order not found.", ct);
            if (order.IsConfirmed) return;
            order.RequirePendingPayment();

            var now = DateTimeOffset.UtcNow;
            if (honourPaymentWindow && order.PaymentWindowIsOver(now))
            {
                await ReleaseStock(order, ct);
                order.Expire(now);
                return;
            }
            if (method == "CARD" && await repository.HasStripeCheckout(id, ct))
                throw new ConflictException("This order has a Stripe Checkout; demo payment is unavailable.");

            decimal costOfGoodsSold = 0;
            foreach (var line in order.Lines)
                costOfGoodsSold += await inventory.ConsumeReserved(line.ProductId, line.Quantity, line.UnitType, ct);

            order.Confirm(reference, now);
            outbox.Publish("OnlineSaleConfirmed", new SaleCompletedEvent(
                id, Guid.Empty, method, order.NetAmount, order.VatAmount, order.TotalAmount, costOfGoodsSold));
        }, ct);
        return await GetOrder(id, ct);
    }

    public Task<OnlineOrderView> CancelOrder(Guid customer, Guid orderId, CancellationToken ct) => Cancel(orderId, customer, ct);

    public async Task<StaffOnlineOrderView> CancelStaffOrder(Guid orderId, CancellationToken ct)
    {
        await Cancel(orderId, null, ct);
        return await GetStaffOrder(orderId, ct);
    }

    /// <summary>Cancels an unpaid order and releases its reserved stock. Cancelling twice is harmless.</summary>
    private async Task<OnlineOrderView> Cancel(Guid id, Guid? customer, CancellationToken ct)
    {
        await unitOfWork.InTransaction(async () =>
        {
            var order = await RequireOrder(id, customer, $"Online order not found: {id}", ct);
            if (order.IsClosedWithoutPayment) return;
            order.RequireUnpaid();
            if (await repository.HasActiveStripeCheckout(id, ct))
                throw new ConflictException("An active Stripe Checkout exists for this order.");

            await ReleaseStock(order, ct);
            order.Cancel(DateTimeOffset.UtcNow);
        }, ct);
        return await GetOrder(id, ct);
    }

    /// <summary>Expires unpaid orders whose payment window has passed (run periodically by the expiry worker).</summary>
    public Task ExpireDueOrders(CancellationToken ct) =>
        unitOfWork.InTransaction(async () =>
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var order in await repository.ListOverdueOrders(now, limit: 100, ct))
            {
                await ReleaseStock(order, ct);
                order.Expire(now);
            }
        }, ct);

    // ---- orders (customer) -----------------------------------------------------------------

    public Task<IReadOnlyList<OnlineOrderView>> ListCustomerOrders(Guid customer, CancellationToken ct) => Orders(customer, null, ct);

    public async Task<OnlineOrderView> GetCustomerOrder(Guid customer, Guid orderId, CancellationToken ct) =>
        (await Orders(customer, orderId, ct)).FirstOrDefault() ?? throw new DomainValidationException($"Online order not found: {orderId}");

    // ---- orders (staff) --------------------------------------------------------------------

    public async Task<IReadOnlyList<StaffOnlineOrderView>> ListStaffOrders(CancellationToken ct)
    {
        var orders = await repository.ListOrders(null, null, ct);
        var stripe = await repository.StripeStatuses(orders.Select(x => x.Id).ToArray(), ct);
        var result = new List<StaffOnlineOrderView>();
        foreach (var order in orders)
            result.Add(EcommerceViews.StaffOrder(order, stripe.GetValueOrDefault(order.Id), await users.FindById(order.CustomerId, ct)));
        return result;
    }

    public async Task<StaffOnlineOrderView> GetStaffOrder(Guid orderId, CancellationToken ct)
    {
        var order = (await repository.ListOrders(null, orderId, ct)).FirstOrDefault()
            ?? throw new DomainValidationException($"Online order not found: {orderId}");
        var stripe = await repository.StripeStatuses([orderId], ct);
        return EcommerceViews.StaffOrder(order, stripe.GetValueOrDefault(orderId), await users.FindById(order.CustomerId, ct));
    }

    public async Task<StaffOnlineOrderView> AdvanceStatus(Guid id, string next, CancellationToken ct)
    {
        var status = OnlineOrderEntity.ParseStaffStatus(next);
        await unitOfWork.InTransaction(async () =>
            (await RequireOrder(id, null, $"Online order not found: {id}", ct)).AdvanceTo(status), ct);
        return await GetStaffOrder(id, ct);
    }

    public async Task<StaffOnlineOrderView> CompletePickup(Guid id, string code, string staff, CancellationToken ct)
    {
        await unitOfWork.InTransaction(async () =>
            (await RequireOrder(id, null, $"Online order not found: {id}", ct)).CompletePickup(code, staff, DateTimeOffset.UtcNow), ct);
        return await GetStaffOrder(id, ct);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private async Task<OnlineOrderView> GetOrder(Guid id, CancellationToken ct) =>
        (await Orders(null, id, ct)).FirstOrDefault() ?? throw new DomainValidationException($"Online order not found: {id}");

    private async Task<IReadOnlyList<OnlineOrderView>> Orders(Guid? customer, Guid? id, CancellationToken ct)
    {
        var orders = await repository.ListOrders(customer, id, ct);
        var stripe = await repository.StripeStatuses(orders.Select(x => x.Id).ToArray(), ct);
        return orders.Select(order => EcommerceViews.Order(order, stripe.GetValueOrDefault(order.Id))).ToList();
    }

    /// <summary>The order, or "not found" when it does not exist or belongs to another customer.</summary>
    private async Task<OnlineOrderEntity> RequireOrder(Guid id, Guid? customer, string notFoundMessage, CancellationToken ct)
    {
        var order = await repository.FindOrder(id, ct);
        if (order is null || (customer is not null && order.CustomerId != customer)) throw new DomainValidationException(notFoundMessage);
        return order;
    }

    private async Task ReleaseStock(OnlineOrderEntity order, CancellationToken ct)
    {
        foreach (var line in order.Lines)
            await inventory.Release(line.ProductId, line.Quantity, line.UnitType, ct);
    }

    private async Task<CartEntity> GetOrCreateCart(Guid customer, CancellationToken ct)
    {
        var cart = await repository.FindCart(customer, ct);
        if (cart is not null) return cart;
        cart = CartEntity.Create(customer, DateTimeOffset.UtcNow);
        repository.Add(cart);
        return cart;
    }

    private async Task<CartEntity> RequireCart(Guid customer, CancellationToken ct) =>
        await repository.FindCart(customer, ct) ?? throw new ConflictException("Cart not found.");

    /// <summary>The product must exist, be sold online and be sold in the requested unit. Returns the normalized unit.</summary>
    private async Task<string> RequireOnlineProduct(Guid productId, string requestedUnit, CancellationToken ct)
    {
        var unit = requestedUnit.Trim().ToUpperInvariant();
        if (unit is not ("EACH" or "WEIGHT")) throw new DomainValidationException("Unit type must be EACH or WEIGHT.");
        var product = await inventory.FindProduct(productId, ct);
        if (product is null || !product.OnlineVisible) throw new DomainValidationException("Product is not available in the online catalog.");
        if (product.UnitType != unit) throw new DomainValidationException("Requested unit type does not match the product.");
        return unit;
    }

    private async Task<CartView> CartView(Guid cartId, CancellationToken ct) => EcommerceViews.Cart(await Rows(cartId, ct));

    /// <summary>The cart's lines with product facts (Inventory), current price (Pricing) and stock (Inventory).</summary>
    private async Task<List<CartRow>> Rows(Guid cartId, CancellationToken ct)
    {
        var items = await repository.ListCartItems(cartId, ct);
        var ids = items.Select(x => x.ProductId).ToArray();
        if (ids.Length == 0) return [];
        var products = await inventory.FindProducts(ids, ct);
        var prices = await pricing.CurrentUnitPrices(ids, ct);
        var available = await inventory.AvailableQuantities(ids, ct);
        return items.Where(x => products.ContainsKey(x.ProductId)).OrderBy(x => products[x.ProductId].Name).Select(x =>
        {
            var product = products[x.ProductId];
            var hasPrice = prices.TryGetValue(x.ProductId, out var price);
            return new CartRow(x.ProductId, product.Name, x.Quantity, x.UnitType, hasPrice ? price : 0, product.VatRate,
                available.GetValueOrDefault(x.ProductId), product.OnlineVisible, hasPrice);
        }).ToList();
    }

    private static int ReadPositive(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], out var value) && value >= 1 ? value
        : configuration[key] is null ? fallback
        : throw new DomainValidationException($"{key} must be at least one minute.");
}
