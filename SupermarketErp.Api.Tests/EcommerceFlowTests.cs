using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SupermarketErp.Api.Tests;

/// <summary>
/// Characterization tests for the online store: catalog -> cart -> checkout (reserves stock) ->
/// payment (consumes stock, posts an online-sale journal) / cancel / expiry (release the reservation).
/// </summary>
public sealed class EcommerceFlowTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private ErpApi Api => fixture.Api;

    // ---- catalog ---------------------------------------------------------------------------

    [Fact]
    public async Task The_catalog_lists_priced_products_with_stock_and_hides_unpriced_ones()
    {
        var priced = await Api.ProductWithStock(price: 10m, quantity: 5, unitCost: 4m);
        var unpriced = await Api.CreateProduct();
        await Api.ReceiveStock(unpriced, 5, 4m);
        using var anonymous = fixture.Factory.CreateClient();

        var product = await anonymous.GetFromJsonAsync<JsonElement>($"/api/store/products/{priced}");
        Assert.Equal("10.000 TND", product.GetProperty("unitPrice").GetString());
        Assert.Equal(19m, product.GetProperty("vatRate").GetDecimal());
        Assert.Equal(5m, product.GetProperty("availableQuantity").GetDecimal());
        Assert.True(product.GetProperty("inStock").GetBoolean());

        var list = await anonymous.GetFromJsonAsync<JsonElement[]>("/api/store/products");
        Assert.Contains(list!, p => p.GetProperty("id").GetGuid() == priced);
        Assert.DoesNotContain(list!, p => p.GetProperty("id").GetGuid() == unpriced);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/store/products/{unpriced}")).StatusCode);
    }

    [Fact]
    public async Task An_active_promotion_lowers_the_storefront_price()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 5, unitCost: 4m);
        await Api.PostOk("/api/pricing/promotions", new
        {
            productId = product, discountPercent = 20m,
            validFrom = DateTimeOffset.UtcNow.AddDays(-1), validUntil = DateTimeOffset.UtcNow.AddDays(1)
        });
        using var anonymous = fixture.Factory.CreateClient();

        var view = await anonymous.GetFromJsonAsync<JsonElement>($"/api/store/products/{product}");

        Assert.Equal("8.000 TND", view.GetProperty("unitPrice").GetString());
    }

    [Fact]
    public async Task Hidden_products_are_not_sold_online()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 5, unitCost: 4m);
        await fixture.ExecuteAsync("UPDATE inventory.product SET online_visible = 0 WHERE id = @id", ("id", product));
        var (customer, _) = await fixture.RegisterCustomer();
        using var anonymous = fixture.Factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/store/products/{product}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Add(customer, product, 1)).StatusCode);
    }

    // ---- cart ------------------------------------------------------------------------------

    [Fact]
    public async Task The_cart_accumulates_quantities_and_totals_with_vat()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 5, unitCost: 4m);
        var (customer, _) = await fixture.RegisterCustomer();

        await Add(customer, product, 1);
        var response = await Add(customer, product, 1);
        var cart = await response.Content.ReadFromJsonAsync<JsonElement>();

        var item = Assert.Single(cart.GetProperty("items").EnumerateArray());
        Assert.Equal("2 unit(s)", item.GetProperty("quantity").GetString());
        Assert.Equal("20.000 TND", item.GetProperty("lineTotal").GetString());
        Assert.Equal("20.000 TND", cart.GetProperty("subtotal").GetString());
        Assert.Equal("3.800 TND", cart.GetProperty("vatAmount").GetString());
        Assert.Equal("23.800 TND", cart.GetProperty("totalAmount").GetString());
    }

    [Fact]
    public async Task Cart_and_orders_are_for_customers_only()
    {
        using var anonymous = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/store/cart")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.GetAsync("/api/store/cart")).StatusCode); // staff token
    }

    // ---- checkout, payment, cancel, expiry -------------------------------------------------

    [Fact]
    public async Task Checkout_reserves_stock_without_removing_it()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 5, unitCost: 4m);
        var (customer, _) = await fixture.RegisterCustomer();
        await Add(customer, product, 2);

        var order = await Checkout(customer);

        Assert.Equal("PENDING_PAYMENT", order.GetProperty("status").GetString());
        Assert.Equal("23.800 TND", order.GetProperty("totalAmount").GetString());
        var stock = await Api.Stock(product);
        Assert.Equal(5m, stock.GetProperty("totalQuantity").GetDecimal());
        Assert.Equal(2m, stock.GetProperty("reservedQuantity").GetDecimal());
        Assert.Equal(3m, stock.GetProperty("availableQuantity").GetDecimal());
        var cart = await customer.GetFromJsonAsync<JsonElement>("/api/store/cart");
        Assert.Empty(cart.GetProperty("items").EnumerateArray()); // the cart is emptied by checkout
    }

    [Fact]
    public async Task Several_customers_can_have_unpaid_orders_at_the_same_time()
    {
        // Regression: payment_reference was NULL UNIQUE, and SQL Server allows only one NULL in a UNIQUE
        // constraint, so a second unpaid order (or any after a cancelled one) failed with a 500.
        var product = await Api.ProductWithStock(price: 10m, quantity: 10, unitCost: 4m);
        var orders = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var (customer, _) = await fixture.RegisterCustomer();
            await Add(customer, product, 1);
            orders.Add((await Checkout(customer)).GetProperty("id").GetGuid());
        }

        Assert.Equal(3, orders.Distinct().Count());
        Assert.Equal(7m, await Api.Available(product));
    }

    [Fact]
    public async Task Checkout_fails_when_stock_is_insufficient_and_reserves_nothing()
    {
        var plentiful = await Api.ProductWithStock(price: 10m, quantity: 10, unitCost: 4m);
        var scarce = await Api.ProductWithStock(price: 10m, quantity: 1, unitCost: 4m);
        var (customer, _) = await fixture.RegisterCustomer();
        await Add(customer, plentiful, 2);
        await Add(customer, scarce, 2);

        var response = await customer.PostAsync("/api/store/checkout", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(10m, await Api.Available(plentiful)); // the first line's reservation was rolled back
        Assert.Equal(1m, await Api.Available(scarce));
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM ecommerce.online_order WHERE total_amount = 47.6"));
    }

    [Fact]
    public async Task Paying_an_order_consumes_stock_and_posts_the_online_sale()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 5, unitCost: 4m);
        var (customer, _) = await fixture.RegisterCustomer();
        await Add(customer, product, 2);
        var order = await Checkout(customer);
        var id = order.GetProperty("id").GetGuid();

        var paid = await Confirm(customer, id);

        Assert.Equal("CONFIRMED", paid.GetProperty("status").GetString());
        var stock = await Api.Stock(product);
        Assert.Equal(3m, stock.GetProperty("totalQuantity").GetDecimal());
        Assert.Equal(0m, stock.GetProperty("reservedQuantity").GetDecimal());

        var journal = await Api.WaitForJournal($"online-sale:{id}");
        ErpApi.AssertBalanced(journal);
        var accounts = ErpApi.Accounts(journal);
        Assert.Equal(23.8m, accounts["CARD_CLEARING"].Debit);
        Assert.Equal(20m, accounts["SALES_REVENUE"].Credit);
        Assert.Equal(3.8m, accounts["OUTPUT_VAT"].Credit);
        Assert.Equal(8m, accounts["COST_OF_GOODS_SOLD"].Debit);
    }

    [Fact]
    public async Task Paying_twice_is_harmless()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 5, unitCost: 4m);
        var (customer, _) = await fixture.RegisterCustomer();
        await Add(customer, product, 2);
        var id = (await Checkout(customer)).GetProperty("id").GetGuid();

        await Confirm(customer, id);
        var again = await Confirm(customer, id);

        Assert.Equal("CONFIRMED", again.GetProperty("status").GetString());
        await Api.WaitForJournal($"online-sale:{id}");
        await Api.WaitForOutboxDrained(id);
        Assert.Equal(3m, await Api.Available(product));
        Assert.Equal(1, await fixture.ScalarAsync<int>(
            "SELECT COUNT(*) FROM finance.journal_entry WHERE event_key = @k", ("k", $"online-sale:{id}")));
    }

    [Fact]
    public async Task Cancelling_an_unpaid_order_releases_the_reservation()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 5, unitCost: 4m);
        var (customer, _) = await fixture.RegisterCustomer();
        await Add(customer, product, 2);
        var id = (await Checkout(customer)).GetProperty("id").GetGuid();
        Assert.Equal(3m, await Api.Available(product));

        var response = await customer.PostAsync($"/api/store/orders/{id}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CANCELLED", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(5m, await Api.Available(product));
    }

    [Fact]
    public async Task A_paid_order_cannot_be_cancelled()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 5, unitCost: 4m);
        var (customer, _) = await fixture.RegisterCustomer();
        await Add(customer, product, 1);
        var id = (await Checkout(customer)).GetProperty("id").GetGuid();
        await Confirm(customer, id);

        var response = await customer.PostAsync($"/api/store/orders/{id}/cancel", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(4m, await Api.Available(product));
    }

    [Fact]
    public async Task An_unpaid_order_expires_and_gives_its_stock_back()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 5, unitCost: 4m);
        var (customer, _) = await fixture.RegisterCustomer();
        await Add(customer, product, 2);
        var id = (await Checkout(customer)).GetProperty("id").GetGuid();
        Assert.Equal(3m, await Api.Available(product));

        await fixture.ExecuteAsync("UPDATE ecommerce.online_order SET expires_at = DATEADD(MINUTE, -5, SYSDATETIMEOFFSET()) WHERE id = @id", ("id", id));

        await ErpApi.Eventually(
            () => customer.GetFromJsonAsync<JsonElement>($"/api/store/orders/{id}"),
            order => order.GetProperty("status").GetString() == "EXPIRED", "the order to expire");
        Assert.Equal(5m, await Api.Available(product));
    }

    [Fact]
    public async Task The_online_sale_that_crosses_the_reorder_point_raises_a_low_stock_alert()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 6, unitCost: 4m, reorderPoint: 5);
        var (customer, _) = await fixture.RegisterCustomer();
        await Add(customer, product, 2); // 6 -> 4
        var id = (await Checkout(customer)).GetProperty("id").GetGuid();

        await Confirm(customer, id);

        await ErpApi.Eventually(() => Api.Alerts(product), alerts => alerts.Length > 0, "a low-stock alert");
    }

    // ---- staff side ------------------------------------------------------------------------

    [Fact]
    public async Task Staff_see_the_customer_and_walk_a_paid_order_to_pickup()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 5, unitCost: 4m);
        var (customer, username) = await fixture.RegisterCustomer();
        await Add(customer, product, 1);
        var id = (await Checkout(customer)).GetProperty("id").GetGuid();
        await Confirm(customer, id);

        var staffView = await Api.GetOk($"/api/online-orders/{id}");
        Assert.Equal(username, staffView.GetProperty("customerUsername").GetString());
        Assert.Equal("Customer " + username, staffView.GetProperty("customerName").GetString());
        Assert.Contains((await Api.GetOk("/api/online-orders")).EnumerateArray(), o => o.GetProperty("id").GetGuid() == id);

        await Patch($"/api/online-orders/{id}/status", new { status = "PREPARING" });
        await Patch($"/api/online-orders/{id}/status", new { status = "READY" });
        var ready = await customer.GetFromJsonAsync<JsonElement>($"/api/store/orders/{id}");
        var code = ready.GetProperty("pickupCode").GetString()!;

        var done = await Api.PostOk($"/api/online-orders/{id}/pickup", new { code });
        Assert.Equal("COMPLETED", done.GetProperty("status").GetString());
        Assert.Equal("admin", done.GetProperty("pickedUpBy").GetString());
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static Task<HttpResponseMessage> Add(HttpClient customer, Guid product, decimal quantity) =>
        customer.PostAsJsonAsync("/api/store/cart/items", new { productId = product, quantityValue = quantity, unitType = "EACH" });

    private static async Task<JsonElement> Checkout(HttpClient customer)
    {
        var response = await customer.PostAsync("/api/store/checkout", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> Confirm(HttpClient customer, Guid orderId)
    {
        var response = await customer.PostAsync($"/api/store/checkout/{orderId}/confirm-payment", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task Patch(string url, object body)
    {
        var (status, json) = await Api.Send(HttpMethod.Patch, url, body);
        Assert.True(status == HttpStatusCode.OK, $"PATCH {url} returned {(int)status}: {json}");
    }
}
