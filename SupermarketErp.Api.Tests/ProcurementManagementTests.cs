using System.Globalization;
using System.Net;
using System.Text.Json;

namespace SupermarketErp.Api.Tests;

/// <summary>
/// Characterization tests for suppliers, purchase-order lifecycle/validation and the low-stock alert list.
/// ReceiptFlowTests covers receiving.
/// </summary>
public sealed class ProcurementManagementTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private ErpApi Api => fixture.Api;

    // ---- suppliers -------------------------------------------------------------------------

    [Fact]
    public async Task Suppliers_can_be_created_read_updated_and_deleted()
    {
        var created = await Api.PostOk("/api/procurement/suppliers",
            new { name = "  Acme Foods ", contactEmail = "buy@acme.test", phone = "70 123 456", address = "Sfax" });
        var id = created.GetProperty("id").GetGuid();

        var supplier = await Api.GetOk($"/api/procurement/suppliers/{id}");
        Assert.Equal("Acme Foods", supplier.GetProperty("name").GetString()); // trimmed
        Assert.Equal("buy@acme.test", supplier.GetProperty("contactEmail").GetString());
        Assert.Equal("70 123 456", supplier.GetProperty("phone").GetString());
        Assert.Contains((await Api.GetOk("/api/procurement/suppliers")).EnumerateArray(), s => s.GetProperty("id").GetGuid() == id);

        Assert.Equal(HttpStatusCode.OK, (await Api.Send(HttpMethod.Put, $"/api/procurement/suppliers/{id}",
            new { name = "Acme Foods Ltd", contactEmail = "new@acme.test" })).Status);
        Assert.Equal("Acme Foods Ltd", (await Api.GetOk($"/api/procurement/suppliers/{id}")).GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.OK, (await Api.Send(HttpMethod.Delete, $"/api/procurement/suppliers/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.Send(HttpMethod.Get, $"/api/procurement/suppliers/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.Send(HttpMethod.Delete, $"/api/procurement/suppliers/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.Send(HttpMethod.Put, $"/api/procurement/suppliers/{id}", new { name = "x" })).Status);
    }

    [Fact]
    public async Task Supplier_input_is_validated()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Post("/api/procurement/suppliers", new { name = "  " })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Post("/api/procurement/suppliers", new { name = new string('x', 256) })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Post("/api/procurement/suppliers", new { name = "ok", address = new string('x', 501) })).Status);
    }

    // ---- purchase orders -------------------------------------------------------------------

    [Fact]
    public async Task A_new_order_is_a_draft_with_its_total_and_appears_in_the_list()
    {
        var first = await Api.CreateProduct();
        var second = await Api.CreateProduct();
        var supplier = await Api.CreateSupplier();

        var order = await Api.CreateOrder(supplier, (first, 10m, 4.5m), (second, 3m, 2m));

        var view = await Api.Order(order);
        Assert.Equal("DRAFT", view.GetProperty("status").GetString());
        Assert.Equal(supplier, view.GetProperty("supplierId").GetGuid());
        Assert.StartsWith("Supplier ", view.GetProperty("supplierName").GetString());
        Assert.Equal(2, view.GetProperty("lines").GetArrayLength());
        Assert.Equal(51m, Invariant(view.GetProperty("totalCost").GetString()!)); // 10 x 4.5 + 3 x 2
        Assert.Contains((await Api.GetOk("/api/procurement/orders")).EnumerateArray(), o => o.GetProperty("id").GetGuid() == order);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.Send(HttpMethod.Get, $"/api/procurement/orders/{Guid.NewGuid()}")).Status);
    }

    [Fact]
    public async Task Order_input_is_validated()
    {
        var product = await Api.CreateProduct("EACH");
        var supplier = await Api.CreateSupplier();

        Assert.Equal(HttpStatusCode.BadRequest, (await CreateOrder(Guid.NewGuid(), Line(product, 1m, "EACH", 1m))).Status);           // unknown supplier
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateOrder(supplier)).Status);                                                   // no lines
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateOrder(supplier, Line(product, 0m, "EACH", 1m))).Status);                   // zero quantity
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateOrder(supplier, Line(product, 1.5m, "EACH", 1m))).Status);                 // fractional EACH
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateOrder(supplier, Line(product, 1m, "BOX", 1m))).Status);                    // unknown unit
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateOrder(supplier, Line(product, 1m, "WEIGHT", 1m))).Status);                 // unit differs from product
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateOrder(supplier, Line(product, 1m, "EACH", -1m))).Status);                  // negative cost
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateOrder(supplier, Line(Guid.NewGuid(), 1m, "EACH", 1m))).Status);           // unknown product
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateOrder(supplier, Line(product, 1m, "EACH", 1m), Line(Guid.NewGuid(), 1m, "EACH", 1m))).Status);
    }

    [Fact]
    public async Task A_weighed_product_is_ordered_with_up_to_three_decimals()
    {
        var product = await Api.CreateProduct("WEIGHT");
        var supplier = await Api.CreateSupplier();

        Assert.Equal(HttpStatusCode.BadRequest, (await CreateOrder(supplier, Line(product, 1.2345m, "WEIGHT", 3m))).Status);
        var created = await CreateOrder(supplier, Line(product, 12.5m, "WEIGHT", 3m));
        Assert.Equal(HttpStatusCode.OK, created.Status);

        var order = created.Body.GetProperty("id").GetGuid();
        var line = (await Api.Order(order)).GetProperty("lines")[0];
        Assert.Equal("WEIGHT", line.GetProperty("unitType").GetString());
        Assert.Equal(12.5m, Invariant(line.GetProperty("quantity").GetString()!));
    }

    [Fact]
    public async Task Order_state_changes_follow_draft_placed_received_and_cancel()
    {
        var product = await Api.CreateProduct();
        var supplier = await Api.CreateSupplier();

        var cancellable = await Api.CreateOrder(supplier, (product, 5m, 2m));
        Assert.Equal(HttpStatusCode.OK, (await Api.Post($"/api/procurement/orders/{cancellable}/cancel")).Status);
        Assert.Equal("CANCELLED", await Api.OrderStatus(cancellable));
        Assert.Equal(HttpStatusCode.Conflict, (await Api.Post($"/api/procurement/orders/{cancellable}/cancel")).Status);

        var placed = await Api.CreateOrder(supplier, (product, 5m, 2m));
        await Api.PlaceOrder(placed);
        Assert.Equal(HttpStatusCode.Conflict, (await Api.Post($"/api/procurement/orders/{placed}/place")).Status);   // already placed
        Assert.Equal(HttpStatusCode.OK, (await Api.Post($"/api/procurement/orders/{placed}/cancel")).Status);          // a placed order can still be cancelled

        var received = await Api.CreateOrder(supplier, (product, 5m, 2m));
        await Api.PlaceOrder(received);
        var line = (await Api.OrderLineIdsByProduct(received))[product];
        await Api.Receive(received, ErpApi.ReceiptLine(line, 5m));
        Assert.Equal(HttpStatusCode.Conflict, (await Api.Post($"/api/procurement/orders/{received}/cancel")).Status);  // too late

        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Post($"/api/procurement/orders/{Guid.NewGuid()}/place")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Post($"/api/procurement/orders/{Guid.NewGuid()}/cancel")).Status);
    }

    [Fact]
    public async Task A_receipt_for_an_unknown_order_or_without_lines_is_rejected()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Post($"/api/procurement/orders/{Guid.NewGuid()}/receive", new { lines = Array.Empty<object>() })).Status);
        var product = await Api.CreateProduct();
        var order = await Api.CreateOrder(await Api.CreateSupplier(), (product, 5m, 2m));
        await Api.PlaceOrder(order);
        var line = (await Api.OrderLineIdsByProduct(order))[product];

        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Post($"/api/procurement/orders/{Guid.NewGuid()}/receive",
            new { lines = new[] { ErpApi.ReceiptLine(line, 5m) } })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Receive(order, ErpApi.ReceiptLine(Guid.NewGuid(), 5m))).Status); // line not on the order
        Assert.Equal("PLACED", await Api.OrderStatus(order));
    }

    // ---- low-stock alerts ------------------------------------------------------------------

    [Fact]
    public async Task The_alert_list_shows_the_product_and_its_current_stock_and_alerts_can_be_acknowledged()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 6, unitCost: 4m, reorderPoint: 5);
        await Api.PostOk("/api/pos/tills", new { tillNumber = "T-" + Guid.NewGuid().ToString("N")[..8] });
        var session = await Api.OpenSession();
        var ticket = await Api.StartTransaction(session);
        await Api.AddLine(ticket, product, 2); // 6 -> 4
        await Api.Complete(ticket, "CARD", 23.8m);
        var followUp = await Api.StartTransaction(session);
        await Api.AddLine(followUp, product, 1); // sold while already low: raises (or repeats) the alert
        await Api.Complete(followUp, "CARD", 11.9m);
        var alerts = await ErpApi.Eventually(() => Api.Alerts(product), found => found.Length > 0, "a low-stock alert");

        var alert = Assert.Single(alerts);
        Assert.Equal("Test product", alert.GetProperty("productName").GetString());
        Assert.Equal(3d, alert.GetProperty("currentStock").GetDouble());
        Assert.Equal(5, alert.GetProperty("reorderPoint").GetInt32());
        Assert.False(alert.GetProperty("acknowledged").GetBoolean());

        var id = alert.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Api.Post($"/api/procurement/alerts/{id}/acknowledge")).Status);
        Assert.True((await Api.Alerts(product)).Single().GetProperty("acknowledged").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Post($"/api/procurement/alerts/{Guid.NewGuid()}/acknowledge")).Status);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static decimal Invariant(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

    private static object Line(Guid product, decimal quantity, string unit, decimal cost) =>
        new { productId = product, quantityValue = quantity, unitType = unit, unitCost = cost };

    private Task<(HttpStatusCode Status, JsonElement Body)> CreateOrder(Guid supplier, params object[] lines) =>
        Api.Post("/api/procurement/orders", new { supplierId = supplier, lines });
}
