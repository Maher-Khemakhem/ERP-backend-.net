using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using SupermarketErp.Api.Features.Auth;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Ecommerce;

public sealed record QuantityRequest(Guid ProductId, decimal QuantityValue, string UnitType);
public sealed record CartItemView(Guid ProductId, string ProductName, string Quantity, string UnitType,
    string UnitPrice, string LineTotal, decimal AvailableQuantity, bool InStock);
public sealed record CartView(IReadOnlyList<CartItemView> Items, string Subtotal, string VatAmount, string TotalAmount);
public sealed record OrderLineView(Guid ProductId, string ProductName, string Quantity, string UnitType,
    string UnitPrice, string LineTotal, string VatAmount);
public sealed record OnlineOrderView(Guid Id, string OrderNumber, string Status, string NetAmount,
    string VatAmount, string TotalAmount, string? PaymentReference, string CreatedAt, string? ConfirmedAt,
    string ExpiresAt, string? CancelledAt, string? ExpiredAt, string? StripeStatus, string? PickupCode,
    IReadOnlyList<OrderLineView> Lines);
public sealed record StaffOnlineOrderView(Guid Id, string OrderNumber, Guid CustomerId, string CustomerUsername,
    string CustomerName, string Status, string NetAmount, string VatAmount, string TotalAmount,
    string? PaymentReference, string CreatedAt, string? ConfirmedAt, string ExpiresAt, string? CancelledAt,
    string? ExpiredAt, string? PickedUpAt, string? PickedUpBy, IReadOnlyList<OrderLineView> Lines);
public sealed record FulfillmentStatusRequest(string Status);
public sealed record PickupRequest(string Code);

public static class StorefrontOrderEndpoints
{
    public static IEndpointRouteBuilder MapStorefrontOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var store = endpoints.MapGroup("/api/store").WithTags("Storefront orders").RequireAuthorization(Roles.Customer);
        store.MapGet("/cart", async (ClaimsPrincipal user, StorefrontOrderService service, CancellationToken ct) =>
            Results.Ok(await service.GetCart(CustomerId(user), ct)));
        store.MapPost("/cart/items", async (ClaimsPrincipal user, QuantityRequest request, StorefrontOrderService service, CancellationToken ct) =>
            Results.Ok(await service.AddCartItem(CustomerId(user), request, ct)));
        store.MapPut("/cart/items/{productId:guid}", async (Guid productId, ClaimsPrincipal user, QuantityRequest request,
            StorefrontOrderService service, CancellationToken ct) =>
        {
            if (productId != request.ProductId) throw new ArgumentException("Product ID in path and body must match.");
            return Results.Ok(await service.ChangeCartItem(CustomerId(user), request, ct));
        });
        store.MapDelete("/cart/items/{productId:guid}", async (Guid productId, ClaimsPrincipal user, StorefrontOrderService service, CancellationToken ct) =>
            Results.Ok(await service.RemoveCartItem(CustomerId(user), productId, ct)));
        store.MapPost("/checkout", async (ClaimsPrincipal user, StorefrontOrderService service, CancellationToken ct) =>
            Results.Json(await service.Checkout(CustomerId(user), ct), statusCode: StatusCodes.Status201Created));
        store.MapPost("/checkout/{orderId:guid}/confirm-payment", async (Guid orderId, ClaimsPrincipal user,
            StorefrontOrderService service, CancellationToken ct) =>
            Results.Ok(await service.ConfirmDemoPayment(CustomerId(user), orderId, ct)));
        store.MapGet("/orders", async (ClaimsPrincipal user, StorefrontOrderService service, CancellationToken ct) =>
            Results.Ok(await service.ListCustomerOrders(CustomerId(user), ct)));
        store.MapGet("/orders/{orderId:guid}", async (Guid orderId, ClaimsPrincipal user, StorefrontOrderService service, CancellationToken ct) =>
            Results.Ok(await service.GetCustomerOrder(CustomerId(user), orderId, ct)));
        store.MapPost("/orders/{orderId:guid}/cancel", async (Guid orderId, ClaimsPrincipal user, StorefrontOrderService service, CancellationToken ct) =>
            Results.Ok(await service.CancelOrder(CustomerId(user), orderId, ct)));

        var staff = endpoints.MapGroup("/api/online-orders").WithTags("Staff online orders")
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);
        staff.MapGet("", async (StorefrontOrderService service, CancellationToken ct) => Results.Ok(await service.ListStaffOrders(ct)));
        staff.MapGet("/{orderId:guid}", async (Guid orderId, StorefrontOrderService service, CancellationToken ct) =>
            Results.Ok(await service.GetStaffOrder(orderId, ct)));
        staff.MapPatch("/{orderId:guid}/status", async (Guid orderId, FulfillmentStatusRequest request,
            StorefrontOrderService service, CancellationToken ct) =>
            Results.Ok(await service.AdvanceStatus(orderId, request.Status, ct)));
        staff.MapPost("/{orderId:guid}/pickup", async (Guid orderId, PickupRequest request, ClaimsPrincipal user,
            StorefrontOrderService service, CancellationToken ct) =>
            Results.Ok(await service.CompletePickup(orderId, request.Code, user.FindFirstValue("sub") ?? "staff", ct)));
        staff.MapPost("/{orderId:guid}/cancel", async (Guid orderId, StorefrontOrderService service, CancellationToken ct) =>
            Results.Ok(await service.CancelStaffOrder(orderId, ct)));
        return endpoints;
    }

    private static Guid CustomerId(ClaimsPrincipal user) => Guid.TryParse(user.FindFirstValue("userId"), out var id)
        ? id : throw new InvalidOperationException("The authenticated token has no user ID.");
}

public sealed class StorefrontOrderService(SqlServerDataSource dataSource, IConfiguration configuration)
{
    private readonly int _paymentTimeoutMinutes = ReadPositive(configuration, "ECOMMERCE_PAYMENT_TIMEOUT_MINUTES", 15);

    public async Task<CartView> GetCart(Guid customerId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var cartId = await GetOrCreateCart(connection, null, customerId, ct);
        return await ReadCart(connection, null, cartId, ct);
    }

    public async Task<CartView> AddCartItem(Guid customerId, QuantityRequest request, CancellationToken ct)
    {
        ValidateQuantity(request.QuantityValue, request.UnitType);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var cartId = await GetOrCreateCart(connection, tx, customerId, ct);
        var unit = await ValidateProduct(connection, tx, request.ProductId, request.UnitType, ct);
        await using var command = new SqlCommand("""
            INSERT INTO ecommerce.cart_item(id,cart_id,product_id,quantity_value,quantity_unit_type)
            VALUES(@id,@cart,@product,@quantity,@unit)
            ON CONFLICT(cart_id,product_id) DO UPDATE SET quantity_value=ecommerce.cart_item.quantity_value+EXCLUDED.quantity_value
            """, connection, tx);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("cart", cartId);
        command.Parameters.AddWithValue("product", request.ProductId); command.Parameters.AddWithValue("quantity", request.QuantityValue);
        command.Parameters.AddWithValue("unit", unit);
        await command.ExecuteNonQueryAsync(ct);
        await TouchCart(connection, tx, cartId, ct);
        var view = await ReadCart(connection, tx, cartId, ct);
        await tx.CommitAsync(ct);
        return view;
    }

    public async Task<CartView> ChangeCartItem(Guid customerId, QuantityRequest request, CancellationToken ct)
    {
        ValidateQuantity(request.QuantityValue, request.UnitType);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var cartId = await RequireCart(connection, tx, customerId, ct);
        var unit = await ValidateProduct(connection, tx, request.ProductId, request.UnitType, ct);
        await using var command = new SqlCommand("UPDATE ecommerce.cart_item SET quantity_value=@quantity,quantity_unit_type=@unit WHERE cart_id=@cart AND product_id=@product", connection, tx);
        command.Parameters.AddWithValue("quantity", request.QuantityValue); command.Parameters.AddWithValue("unit", unit);
        command.Parameters.AddWithValue("cart", cartId); command.Parameters.AddWithValue("product", request.ProductId);
        if (await command.ExecuteNonQueryAsync(ct) == 0) throw new InvalidOperationException("Cart item not found.");
        await TouchCart(connection, tx, cartId, ct);
        var view = await ReadCart(connection, tx, cartId, ct);
        await tx.CommitAsync(ct);
        return view;
    }

    public async Task<CartView> RemoveCartItem(Guid customerId, Guid productId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var cartId = await RequireCart(connection, tx, customerId, ct);
        await using var command = new SqlCommand("DELETE FROM ecommerce.cart_item WHERE cart_id=@cart AND product_id=@product", connection, tx);
        command.Parameters.AddWithValue("cart", cartId); command.Parameters.AddWithValue("product", productId);
        await command.ExecuteNonQueryAsync(ct);
        await TouchCart(connection, tx, cartId, ct);
        var view = await ReadCart(connection, tx, cartId, ct);
        await tx.CommitAsync(ct);
        return view;
    }

    public async Task<OnlineOrderView> Checkout(Guid customerId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var cartId = await GetOrCreateCart(connection, tx, customerId, ct);
        await using (var lockCart = new SqlCommand("SELECT id FROM ecommerce.cart WHERE id=@id FOR UPDATE", connection, tx))
        { lockCart.Parameters.AddWithValue("id", cartId); await lockCart.ExecuteScalarAsync(ct); }
        var cart = await ReadPricedCartRows(connection, tx, cartId, ct);
        if (cart.Count == 0) throw new InvalidOperationException("Cannot check out an empty cart.");
        if (cart.Any(item => !item.OnlineVisible || !item.HasPrice))
            throw new InvalidOperationException("The cart contains a product that is no longer available online.");
        var orderId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var orderNumber = "ONL-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        decimal net = 0, vat = 0;
        foreach (var item in cart)
        {
            var lineNet = Money(item.UnitPrice * item.Quantity);
            var lineVat = Money(lineNet * item.VatRate / 100m);
            net += lineNet; vat += lineVat;
        }
        net = Money(net); vat = Money(vat);
        await using (var order = new SqlCommand("""
            INSERT INTO ecommerce.online_order(id,order_number,customer_id,status,net_amount,vat_amount,total_amount,payment_reference,created_at,confirmed_at,version,expires_at,cancelled_at,expired_at,pickup_code,picked_up_at,picked_up_by)
            VALUES(@id,@number,@customer,'PENDING_PAYMENT',@net,@vat,@total,NULL,@now,NULL,0,@expires,NULL,NULL,NULL,NULL,NULL)
            """, connection, tx))
        {
            order.Parameters.AddWithValue("id", orderId); order.Parameters.AddWithValue("number", orderNumber);
            order.Parameters.AddWithValue("customer", customerId); order.Parameters.AddWithValue("net", net);
            order.Parameters.AddWithValue("vat", vat); order.Parameters.AddWithValue("total", net + vat);
            order.Parameters.AddWithValue("now", now); order.Parameters.AddWithValue("expires", now.AddMinutes(_paymentTimeoutMinutes));
            await order.ExecuteNonQueryAsync(ct);
        }
        foreach (var item in cart)
        {
            var lineNet = Money(item.UnitPrice * item.Quantity);
            var lineVat = Money(lineNet * item.VatRate / 100m);
            await ReserveStock(connection, tx, item.ProductId, item.Quantity, item.UnitType, ct);
            await using var line = new SqlCommand("""
                INSERT INTO ecommerce.online_order_line(id,online_order_id,product_id,product_name,quantity_value,quantity_unit_type,unit_price,line_total,vat_amount)
                VALUES(@id,@order,@product,@name,@quantity,@unit,@price,@total,@vat)
                """, connection, tx);
            line.Parameters.AddWithValue("id", Guid.NewGuid()); line.Parameters.AddWithValue("order", orderId);
            line.Parameters.AddWithValue("product", item.ProductId); line.Parameters.AddWithValue("name", item.Name);
            line.Parameters.AddWithValue("quantity", item.Quantity); line.Parameters.AddWithValue("unit", item.UnitType);
            line.Parameters.AddWithValue("price", item.UnitPrice); line.Parameters.AddWithValue("total", lineNet + lineVat);
            line.Parameters.AddWithValue("vat", lineVat); await line.ExecuteNonQueryAsync(ct);
        }
        await using (var clear = new SqlCommand("DELETE FROM ecommerce.cart_item WHERE cart_id=@id", connection, tx))
        { clear.Parameters.AddWithValue("id", cartId); await clear.ExecuteNonQueryAsync(ct); }
        await TouchCart(connection, tx, cartId, ct);
        await tx.CommitAsync(ct);
        return await GetOrder(orderId, ct);
    }

    public Task<OnlineOrderView> ConfirmDemoPayment(Guid customerId, Guid orderId, CancellationToken ct) =>
        ConfirmPayment(orderId, customerId, $"MOCK-CARD-{orderId}", "CARD", true, ct);

    public Task<OnlineOrderView> ConfirmStripePayment(Guid orderId, string paymentReference, CancellationToken ct) =>
        ConfirmPayment(orderId, null, paymentReference, "STRIPE", false, ct);

    private async Task<OnlineOrderView> ConfirmPayment(Guid orderId, Guid? customerId, string paymentReference,
        string paymentMethod, bool expireIfDue, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var order = await ReadOrderHeader(connection, tx, orderId, ct);
        if (order is null || (customerId is not null && order.CustomerId != customerId)) throw new ArgumentException("Order not found.");
        if (order.Status == "CONFIRMED") { await tx.CommitAsync(ct); return await GetOrder(orderId, ct); }
        if (order.Status != "PENDING_PAYMENT") throw new InvalidOperationException("Only pending orders can be paid.");
        if (expireIfDue && order.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            var expiredLines = await ReadOrderLineRows(connection, tx, orderId, ct);
            foreach (var line in expiredLines) await ReleaseReservedStock(connection, tx, line.ProductId, line.Quantity, line.UnitType, ct);
            await using var expire = new SqlCommand("UPDATE ecommerce.online_order SET status='EXPIRED',expired_at=@now,version=version+1 WHERE id=@id", connection, tx);
            expire.Parameters.AddWithValue("now", DateTimeOffset.UtcNow); expire.Parameters.AddWithValue("id", orderId);
            await expire.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return await GetOrder(orderId, ct);
        }
        if (paymentMethod == "CARD")
            await EnsureNoStripeCheckout(connection, tx, orderId, ct);
        var lines = await ReadOrderLineRows(connection, tx, orderId, ct);
        decimal cogs = 0;
        foreach (var line in lines)
            cogs += await ConsumeReservedStock(connection, tx, line.ProductId, line.Quantity, line.UnitType, ct);
        await using (var update = new SqlCommand("UPDATE ecommerce.online_order SET status='CONFIRMED',payment_reference=@reference,confirmed_at=@now,version=version+1 WHERE id=@id", connection, tx))
        {
            update.Parameters.AddWithValue("reference", paymentReference); update.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
            update.Parameters.AddWithValue("id", orderId); await update.ExecuteNonQueryAsync(ct);
        }
        await OutboxWriter.Write(connection, tx, "OnlineSaleConfirmed",
            new SaleCompletedEvent(orderId, Guid.Empty, paymentMethod, order.Net, order.Vat, order.Total, cogs), ct);
        await tx.CommitAsync(ct);
        return await GetOrder(orderId, ct);
    }

    public async Task<IReadOnlyList<OnlineOrderView>> ListCustomerOrders(Guid customerId, CancellationToken ct) =>
        await ListOrders("WHERE customer_id=@customer", customerId, ct);

    public async Task<OnlineOrderView> GetCustomerOrder(Guid customerId, Guid orderId, CancellationToken ct)
    {
        var list = await ListOrders("WHERE id=@order AND customer_id=@customer", customerId, ct, orderId);
        return list.FirstOrDefault() ?? throw new ArgumentException($"Online order not found: {orderId}");
    }

    public async Task<IReadOnlyList<StaffOnlineOrderView>> ListStaffOrders(CancellationToken ct)
    {
        var orders = await ListOrders("", null, ct);
        var result = new List<StaffOnlineOrderView>();
        foreach (var order in orders)
        {
            var identity = await FindCustomer(order.Id, ct);
            result.Add(ToStaff(order, identity.Id, identity.Username, identity.DisplayName, identity.PickedUpAt, identity.PickedUpBy));
        }
        return result;
    }

    public async Task<StaffOnlineOrderView> GetStaffOrder(Guid id, CancellationToken ct)
    {
        var order = await GetOrder(id, ct);
        var identity = await FindCustomer(id, ct);
        return ToStaff(order, identity.Id, identity.Username, identity.DisplayName, identity.PickedUpAt, identity.PickedUpBy);
    }

    public async Task<OnlineOrderView> CancelOrder(Guid customerId, Guid id, CancellationToken ct) =>
        await Cancel(id, customerId, ct);

    public Task<StaffOnlineOrderView> CancelStaffOrder(Guid id, CancellationToken ct) => CancelForStaff(id, ct);

    public async Task ExpireDueOrders(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var ids = new List<Guid>();
        await using (var select = new SqlCommand("""
            SELECT id FROM ecommerce.online_order WHERE status='PENDING_PAYMENT' AND expires_at<=@now
            ORDER BY expires_at LIMIT 100 FOR UPDATE SKIP LOCKED
            """, connection, tx))
        {
            select.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        }
        foreach (var id in ids)
        {
            var lines = await ReadOrderLineRows(connection, tx, id, ct);
            foreach (var line in lines) await ReleaseReservedStock(connection, tx, line.ProductId, line.Quantity, line.UnitType, ct);
            await using var update = new SqlCommand("UPDATE ecommerce.online_order SET status='EXPIRED',expired_at=@now,version=version+1 WHERE id=@id AND status='PENDING_PAYMENT'", connection, tx);
            update.Parameters.AddWithValue("now", DateTimeOffset.UtcNow); update.Parameters.AddWithValue("id", id);
            await update.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<StaffOnlineOrderView> AdvanceStatus(Guid id, string next, CancellationToken ct)
    {
        var desired = next.Trim().ToUpperInvariant();
        if (desired is not ("PREPARING" or "READY")) throw new ArgumentException("Staff can advance orders to PREPARING or READY.");
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var current = await ReadOrderHeader(connection, tx, id, ct);
        if (current is null) throw new ArgumentException($"Online order not found: {id}");
        var allowed = (current.Status == "CONFIRMED" && desired == "PREPARING") || (current.Status == "PREPARING" && desired == "READY");
        if (!allowed) throw new InvalidOperationException($"Online order cannot move from {current.Status} to {desired}.");
        var code = desired == "READY" ? Convert.ToHexString(RandomNumberGenerator.GetBytes(6)) : null;
        await using var update = new SqlCommand("UPDATE ecommerce.online_order SET status=@status,pickup_code=@code,version=version+1 WHERE id=@id", connection, tx);
        update.Parameters.AddWithValue("status", desired); update.Parameters.AddWithValue("code", (object?)code ?? DBNull.Value);
        update.Parameters.AddWithValue("id", id); await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return await GetStaffOrder(id, ct);
    }

    public async Task<StaffOnlineOrderView> CompletePickup(Guid id, string code, string staff, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var current = await ReadOrderHeader(connection, tx, id, ct);
        if (current is null) throw new ArgumentException($"Online order not found: {id}");
        if (current.Status != "READY" || string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Only ready orders can be collected with a pickup code.");
        var normalized = code.Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(normalized), System.Text.Encoding.ASCII.GetBytes(current.PickupCode ?? "")))
            throw new ArgumentException("Invalid pickup code.");
        await using var update = new SqlCommand("UPDATE ecommerce.online_order SET status='COMPLETED',pickup_code=NULL,picked_up_at=@now,picked_up_by=@staff,version=version+1 WHERE id=@id", connection, tx);
        update.Parameters.AddWithValue("now", DateTimeOffset.UtcNow); update.Parameters.AddWithValue("staff", staff);
        update.Parameters.AddWithValue("id", id); await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return await GetStaffOrder(id, ct);
    }

    private async Task<StaffOnlineOrderView> CancelForStaff(Guid id, CancellationToken ct)
    {
        var order = await Cancel(id, null, ct);
        var identity = await FindCustomer(id, ct);
        return ToStaff(order, identity.Id, identity.Username, identity.DisplayName, identity.PickedUpAt, identity.PickedUpBy);
    }

    private async Task<OnlineOrderView> Cancel(Guid id, Guid? customerId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var order = await ReadOrderHeader(connection, tx, id, ct);
        if (order is null || (customerId is not null && order.CustomerId != customerId))
            throw new ArgumentException($"Online order not found: {id}");
        if (order.Status is "CANCELLED" or "EXPIRED") { await tx.CommitAsync(ct); return await GetOrder(id, ct); }
        if (order.Status != "PENDING_PAYMENT") throw new InvalidOperationException("Only unpaid orders can be cancelled.");
        await EnsureNoActiveStripeCheckout(connection, tx, id, ct);
        var lines = await ReadOrderLineRows(connection, tx, id, ct);
        foreach (var line in lines) await ReleaseReservedStock(connection, tx, line.ProductId, line.Quantity, line.UnitType, ct);
        await using var update = new SqlCommand("UPDATE ecommerce.online_order SET status='CANCELLED',cancelled_at=@now,version=version+1 WHERE id=@id", connection, tx);
        update.Parameters.AddWithValue("now", DateTimeOffset.UtcNow); update.Parameters.AddWithValue("id", id);
        await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return await GetOrder(id, ct);
    }

    private async Task<OnlineOrderView> GetOrder(Guid id, CancellationToken ct)
    {
        var orders = await ListOrders("WHERE id=@order", null, ct, id);
        return orders.FirstOrDefault() ?? throw new ArgumentException($"Online order not found: {id}");
    }

    private async Task<IReadOnlyList<OnlineOrderView>> ListOrders(string filter, Guid? customer, CancellationToken ct, Guid? order = null)
    {
        var heads = new List<OrderHead>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var sql = """
            SELECT o.id,o.order_number,o.customer_id,o.status,o.net_amount,o.vat_amount,o.total_amount,
                   o.payment_reference,o.created_at,o.confirmed_at,o.expires_at,o.cancelled_at,o.expired_at,
                   o.pickup_code,(SELECT sc.status FROM ecommerce.stripe_checkout sc WHERE sc.order_id=o.id)
            FROM ecommerce.online_order o
            """ + filter + " ORDER BY o.created_at DESC";
        await using (var command = new SqlCommand(sql, connection))
        {
            if (customer is not null) command.Parameters.AddWithValue("customer", customer.Value);
            if (order is not null) command.Parameters.AddWithValue("order", order.Value);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) heads.Add(ReadHead(reader));
        }
        var list = new List<OnlineOrderView>(heads.Count);
        foreach (var head in heads) list.Add(await ToView(head, connection, ct));
        return list;
    }

    private async Task<OnlineOrderView> ToView(OrderHead h, SqlConnection connection, CancellationToken ct)
    {
        var lines = new List<OrderLineView>();
        await using var command = new SqlCommand("SELECT product_id,product_name,quantity_value,quantity_unit_type,unit_price,line_total,vat_amount FROM ecommerce.online_order_line WHERE online_order_id=@id ORDER BY id", connection);
        command.Parameters.AddWithValue("id", h.Id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            lines.Add(new OrderLineView(reader.GetGuid(0), reader.GetString(1), FormatQuantity(reader.GetDecimal(2), reader.GetString(3)),
                reader.GetString(3), FormatMoney(reader.GetDecimal(4)), FormatMoney(reader.GetDecimal(5)), FormatMoney(reader.GetDecimal(6))));
        return h.ToView(lines);
    }

    private async Task<OrderHead?> ReadOrderHeader(SqlConnection c, SqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT id,order_number,customer_id,status,net_amount,vat_amount,total_amount,payment_reference,created_at,confirmed_at,expires_at,cancelled_at,expired_at,pickup_code FROM ecommerce.online_order WHERE id=@id FOR UPDATE", c, tx);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadHead(reader) : null;
    }

    private async Task<List<OrderLineRow>> ReadOrderLineRows(SqlConnection c, SqlTransaction tx, Guid orderId, CancellationToken ct)
    {
        var lines = new List<OrderLineRow>();
        await using var command = new SqlCommand("SELECT product_id,quantity_value,quantity_unit_type FROM ecommerce.online_order_line WHERE online_order_id=@id", c, tx);
        command.Parameters.AddWithValue("id", orderId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) lines.Add(new OrderLineRow(reader.GetGuid(0), reader.GetDecimal(1), reader.GetString(2)));
        return lines;
    }

    private static async Task EnsureNoActiveStripeCheckout(SqlConnection c, SqlTransaction tx, Guid orderId, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT EXISTS(SELECT 1 FROM ecommerce.stripe_checkout WHERE order_id=@id AND status IN ('CREATING','PENDING'))", c, tx);
        command.Parameters.AddWithValue("id", orderId);
        if ((bool)(await command.ExecuteScalarAsync(ct) ?? false))
            throw new InvalidOperationException("An active Stripe Checkout exists for this order.");
    }

    private static async Task EnsureNoStripeCheckout(SqlConnection c, SqlTransaction tx, Guid orderId, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT EXISTS(SELECT 1 FROM ecommerce.stripe_checkout WHERE order_id=@id)", c, tx);
        command.Parameters.AddWithValue("id", orderId);
        if ((bool)(await command.ExecuteScalarAsync(ct) ?? false))
            throw new InvalidOperationException("This order has a Stripe Checkout; demo payment is unavailable.");
    }

    private async Task<(Guid Id, string Username, string DisplayName, string? PickedUpAt, string? PickedUpBy)> FindCustomer(Guid orderId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new SqlCommand("SELECT u.id,u.username,u.display_name,o.picked_up_at,o.picked_up_by FROM ecommerce.online_order o JOIN dbo.app_user u ON u.id=o.customer_id WHERE o.id=@id", connection);
        command.Parameters.AddWithValue("id", orderId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : JavaInstantFormat.Format(AsInstant(reader.GetDateTime(3))), reader.IsDBNull(4) ? null : reader.GetString(4))
            : (Guid.Empty, "Unknown", "Unknown customer", null, null);
    }

    private static async Task<Guid> GetOrCreateCart(SqlConnection c, SqlTransaction? tx, Guid customer, CancellationToken ct)
    {
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await using var command = new SqlCommand("INSERT INTO ecommerce.cart(id,customer_id,version,created_at,updated_at) VALUES(@id,@customer,0,@now,@now) ON CONFLICT(customer_id) DO UPDATE SET customer_id=EXCLUDED.customer_id RETURNING id", c, tx);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("customer", customer); command.Parameters.AddWithValue("now", now);
        return (Guid)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<Guid> RequireCart(SqlConnection c, SqlTransaction tx, Guid customer, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT id FROM ecommerce.cart WHERE customer_id=@customer FOR UPDATE", c, tx);
        command.Parameters.AddWithValue("customer", customer);
        return (Guid)(await command.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("Cart not found."));
    }

    private static async Task TouchCart(SqlConnection c, SqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var command = new SqlCommand("UPDATE ecommerce.cart SET updated_at=@now,version=version+1 WHERE id=@id", c, tx);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow); command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<CartView> ReadCart(SqlConnection c, SqlTransaction? tx, Guid id, CancellationToken ct)
    {
        var rows = await ReadPricedCartRows(c, tx, id, ct);
        var items = new List<CartItemView>(); decimal subtotal = 0, vat = 0;
        foreach (var row in rows)
        {
            var line = Money(row.UnitPrice * row.Quantity);
            var tax = Money(line * row.VatRate / 100m);
            subtotal += line; vat += tax;
            items.Add(new CartItemView(row.ProductId, row.Name, FormatQuantity(row.Quantity, row.UnitType), row.UnitType,
                FormatMoney(row.UnitPrice), FormatMoney(line), row.Available, row.Available > 0));
        }
        subtotal = Money(subtotal); vat = Money(vat);
        return new CartView(items, FormatMoney(subtotal), FormatMoney(vat), FormatMoney(subtotal + vat));
    }

    private static async Task<List<CartRow>> ReadPricedCartRows(SqlConnection c, SqlTransaction? tx, Guid cartId, CancellationToken ct)
    {
        const string sql = """
            SELECT ci.product_id,p.name,ci.quantity_value,ci.quantity_unit_type,
                   COALESCE(price.unit_price,0)*(1-ROUND(COALESCE(promo.discount_percent,0)/100,4)),
                   COALESCE(p.vat_rate,19),COALESCE(stock.available,0),p.online_visible,price.unit_price IS NOT NULL
            FROM ecommerce.cart_item ci JOIN inventory.product p ON p.id=ci.product_id
            LEFT JOIN pricing.product_price price ON price.product_id=p.id
            LEFT JOIN LATERAL (SELECT discount_percent FROM pricing.promotion pr WHERE pr.product_id=p.id AND pr.active AND pr.valid_from<=@now AND pr.valid_until>=@now ORDER BY pr.valid_from LIMIT 1) promo ON 1
            LEFT JOIN LATERAL (SELECT SUM(le.on_hand_value-le.reserved_value) available FROM inventory.stock_level sl JOIN inventory.lot_entry le ON le.stock_level_id=sl.id WHERE sl.product_id=p.id) stock ON 1
            WHERE ci.cart_id=@cart ORDER BY p.name
            """;
        var list = new List<CartRow>();
        await using var command = new SqlCommand(sql, c, tx);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow); command.Parameters.AddWithValue("cart", cartId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(new CartRow(reader.GetGuid(0), reader.GetString(1), reader.GetDecimal(2),
            reader.GetString(3), reader.GetDecimal(4), reader.GetDecimal(5), reader.GetDecimal(6), reader.GetBoolean(7), reader.GetBoolean(8)));
        return list;
    }

    private static async Task<string> ValidateProduct(SqlConnection c, SqlTransaction tx, Guid productId, string requested, CancellationToken ct)
    {
        var unit = requested.Trim().ToUpperInvariant();
        if (unit is not ("EACH" or "WEIGHT")) throw new ArgumentException("Unit type must be EACH or WEIGHT.");
        await using var command = new SqlCommand("SELECT unit_type,online_visible FROM inventory.product WHERE id=@id", c, tx);
        command.Parameters.AddWithValue("id", productId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || !reader.GetBoolean(1)) throw new ArgumentException("Product is not available in the online catalog.");
        if (!reader.GetString(0).Equals(unit, StringComparison.Ordinal)) throw new ArgumentException("Requested unit type does not match the product.");
        return unit;
    }

    private static void ValidateQuantity(decimal value, string unit)
    {
        if (value <= 0) throw new ArgumentException("Quantity must be positive.");
        var type = unit.Trim().ToUpperInvariant();
        if (type is not ("EACH" or "WEIGHT")) throw new ArgumentException("Unit type must be EACH or WEIGHT.");
        if (type == "EACH" && value != decimal.Truncate(value)) throw new ArgumentException("EACH quantity must be a whole number.");
        if (type == "WEIGHT" && decimal.Round(value, 3) != value) throw new ArgumentException("WEIGHT quantity supports three decimal places.");
    }

    private static async Task ReserveStock(SqlConnection c, SqlTransaction tx, Guid productId, decimal quantity, string unitType, CancellationToken ct)
    {
        var lots = await LockLots(c, tx, productId, unitType, ct);
        var available = lots.Sum(x => x.OnHand - x.Reserved);
        if (available < quantity) throw new InvalidOperationException($"Insufficient available stock for product {productId}.");
        var remain = quantity;
        foreach (var lot in lots)
        {
            if (remain <= 0) break;
            var reserve = Math.Min(remain, lot.OnHand - lot.Reserved);
            if (reserve <= 0) continue;
            await using var update = new SqlCommand("UPDATE inventory.lot_entry SET reserved_value=reserved_value+@q WHERE id=@id", c, tx);
            update.Parameters.AddWithValue("q", reserve); update.Parameters.AddWithValue("id", lot.EntryId); await update.ExecuteNonQueryAsync(ct);
            remain -= reserve;
        }
        await BumpStockVersion(c, tx, productId, ct);
    }

    private static async Task<decimal> ConsumeReservedStock(SqlConnection c, SqlTransaction tx, Guid productId, decimal quantity, string unit, CancellationToken ct)
    {
        var lots = await LockLots(c, tx, productId, unit, ct);
        if (lots.Sum(x => x.Reserved) < quantity) throw new InvalidOperationException("Reserved stock is insufficient.");
        var remain = quantity; decimal cost = 0;
        foreach (var lot in lots.Where(x => x.Reserved > 0))
        {
            if (remain <= 0) break;
            var take = Math.Min(remain, lot.Reserved);
            await using var update = new SqlCommand("UPDATE inventory.lot_entry SET on_hand_value=on_hand_value-@q,reserved_value=reserved_value-@q WHERE id=@id", c, tx);
            update.Parameters.AddWithValue("q", take); update.Parameters.AddWithValue("id", lot.EntryId); await update.ExecuteNonQueryAsync(ct);
            cost = Money(cost + take * lot.UnitCost); remain -= take;
        }
        await BumpStockVersion(c, tx, productId, ct);
        await using (var check = new SqlCommand("""
            SELECT COALESCE(p.reorder_point,0),COALESCE(SUM(le.on_hand_value-le.reserved_value),0)
            FROM inventory.product p
            LEFT JOIN inventory.stock_level sl ON sl.product_id=p.id
            LEFT JOIN inventory.lot_entry le ON le.stock_level_id=sl.id
            WHERE p.id=@id GROUP BY p.id
            """, c, tx))
        {
            check.Parameters.AddWithValue("id", productId);
            await using var reader = await check.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct) && reader.GetDecimal(1) <= reader.GetInt32(0))
            {
                await reader.CloseAsync();
                await OutboxWriter.Write(c, tx, "LowStockThresholdReached", productId, ct);
            }
        }
        return cost;
    }

    private static async Task ReleaseReservedStock(SqlConnection c, SqlTransaction tx, Guid productId, decimal quantity, string unit, CancellationToken ct)
    {
        var lots = await LockLots(c, tx, productId, unit, ct);
        if (lots.Sum(x => x.Reserved) < quantity) throw new InvalidOperationException("Reserved stock is insufficient to release.");
        var remain = quantity;
        foreach (var lot in lots.Where(x => x.Reserved > 0))
        {
            if (remain <= 0) break;
            var release = Math.Min(remain, lot.Reserved);
            await using var update = new SqlCommand("UPDATE inventory.lot_entry SET reserved_value=reserved_value-@q WHERE id=@id", c, tx);
            update.Parameters.AddWithValue("q", release); update.Parameters.AddWithValue("id", lot.EntryId); await update.ExecuteNonQueryAsync(ct);
            remain -= release;
        }
        await BumpStockVersion(c, tx, productId, ct);
    }

    private static async Task<List<StockLot>> LockLots(SqlConnection c, SqlTransaction tx, Guid productId, string unit, CancellationToken ct)
    {
        var result = new List<StockLot>();
        var unitCode = unit == "EACH" ? (short)0 : (short)1;
        await using var stock = new SqlCommand("SELECT id FROM inventory.stock_level WHERE product_id=@id FOR UPDATE", c, tx);
        stock.Parameters.AddWithValue("id", productId);
        var stockId = await stock.ExecuteScalarAsync(ct);
        if (stockId is null) throw new InvalidOperationException($"No stock exists for product {productId}.");
        await using var command = new SqlCommand("""
            SELECT le.id,le.on_hand_value,le.reserved_value,l.unit_cost
            FROM inventory.lot_entry le JOIN inventory.lot l ON l.id=le.lot_id
            WHERE le.stock_level_id=@id AND le.on_hand_unit_type=@unit
            ORDER BY l.expiry_date NULLS LAST,l.received_date,l.id FOR UPDATE OF le
            """, c, tx);
        command.Parameters.AddWithValue("id", (Guid)stockId); command.Parameters.AddWithValue("unit", unitCode);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new StockLot(reader.GetGuid(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3)));
        return result;
    }

    private static async Task BumpStockVersion(SqlConnection c, SqlTransaction tx, Guid productId, CancellationToken ct)
    {
        await using var command = new SqlCommand("UPDATE inventory.stock_level SET version=version+1 WHERE product_id=@id", c, tx);
        command.Parameters.AddWithValue("id", productId); await command.ExecuteNonQueryAsync(ct);
    }

    private static OrderHead ReadHead(SqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3),
        r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6), r.IsDBNull(7) ? null : r.GetString(7),
        AsInstant(r.GetDateTime(8)), r.IsDBNull(9) ? null : AsInstant(r.GetDateTime(9)), AsInstant(r.GetDateTime(10)),
        r.IsDBNull(11) ? null : AsInstant(r.GetDateTime(11)), r.IsDBNull(12) ? null : AsInstant(r.GetDateTime(12)),
        r.IsDBNull(13) ? null : r.GetString(13), r.FieldCount > 14 && !r.IsDBNull(14) ? r.GetString(14) : null);

    private static StaffOnlineOrderView ToStaff(OnlineOrderView o, Guid customerId, string username, string name, string? pickedUpAt, string? pickedUpBy) =>
        new(o.Id, o.OrderNumber, customerId, username, name, o.Status, o.NetAmount, o.VatAmount, o.TotalAmount,
            o.PaymentReference, o.CreatedAt, o.ConfirmedAt, o.ExpiresAt, o.CancelledAt, o.ExpiredAt, pickedUpAt, pickedUpBy, o.Lines);

    private static string FormatMoney(decimal value) => $"{value.ToString("0.000", CultureInfo.InvariantCulture)} TND";
    private static decimal Money(decimal value) => decimal.Round(value, 3, MidpointRounding.AwayFromZero);
    private static int ReadPositive(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], out var value) && value >= 1
            ? value
            : configuration[key] is null ? fallback : throw new ArgumentException($"{key} must be at least one minute.");
    private static string FormatQuantity(decimal value, string unit) => unit == "EACH" ? $"{value:0} unit(s)" : $"{value:0.000} kg";
    private static DateTimeOffset AsInstant(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record CartRow(Guid ProductId, string Name, decimal Quantity, string UnitType, decimal UnitPrice,
        decimal VatRate, decimal Available, bool OnlineVisible, bool HasPrice);
    private sealed record OrderLineRow(Guid ProductId, decimal Quantity, string UnitType);
    private sealed record StockLot(Guid EntryId, decimal OnHand, decimal Reserved, decimal UnitCost);
    private sealed record OrderHead(Guid Id, string Number, Guid CustomerId, string Status, decimal Net, decimal Vat,
        decimal Total, string? PaymentReference, DateTimeOffset CreatedAt, DateTimeOffset? ConfirmedAt,
        DateTimeOffset ExpiresAt, DateTimeOffset? CancelledAt, DateTimeOffset? ExpiredAt, string? PickupCode,
        string? StripeStatus = null)
    {
        public OnlineOrderView ToView(IReadOnlyList<OrderLineView> lines) => new(Id, Number, Status, FormatMoney(Net),
            FormatMoney(Vat), FormatMoney(Total), PaymentReference, JavaInstantFormat.Format(CreatedAt), ConfirmedAt is null ? null : JavaInstantFormat.Format(ConfirmedAt.Value),
            JavaInstantFormat.Format(ExpiresAt), CancelledAt is null ? null : JavaInstantFormat.Format(CancelledAt.Value), ExpiredAt is null ? null : JavaInstantFormat.Format(ExpiredAt.Value), StripeStatus,
            PickupCode is null ? null : $"{PickupCode[..6]}-{PickupCode[6..]}", lines);
    }
}

public sealed class OnlineOrderExpiryWorker(IServiceScopeFactory scopeFactory, ILogger<OnlineOrderExpiryWorker> logger,
    IConfiguration configuration) : BackgroundService
{
    private readonly int _scanIntervalMs = int.TryParse(configuration["ECOMMERCE_EXPIRY_SCAN_INTERVAL_MS"], out var interval)
        && interval > 0 ? interval : configuration["ECOMMERCE_EXPIRY_SCAN_INTERVAL_MS"] is null ? 60_000
        : throw new ArgumentException("ECOMMERCE_EXPIRY_SCAN_INTERVAL_MS must be positive.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<StorefrontOrderService>();
                await service.ExpireDueOrders(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Failed to expire online orders"); }
            await Task.Delay(TimeSpan.FromMilliseconds(_scanIntervalMs), stoppingToken);
        }
    }
}

