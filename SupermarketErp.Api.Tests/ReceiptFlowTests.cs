using System.Net;

namespace SupermarketErp.Api.Tests;

/// <summary>
/// Characterization tests for purchasing: draft -> placed -> received, which creates stock lots
/// and (through the outbox) a goods-received-not-invoiced journal entry.
/// </summary>
public sealed class ReceiptFlowTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private ErpApi Api => fixture.Api;

    [Fact]
    public async Task Receiving_a_placed_order_creates_stock_and_posts_inventory_against_grni()
    {
        var product = await Api.CreateProduct();
        var order = await Api.CreateOrder(await Api.CreateSupplier(), (product, 10m, 4.5m));
        Assert.Equal("DRAFT", await Api.OrderStatus(order));
        await Api.PlaceOrder(order);
        Assert.Equal("PLACED", await Api.OrderStatus(order));
        var lines = await Api.OrderLineIdsByProduct(order);

        var (status, body) = await Api.Receive(order, ErpApi.ReceiptLine(lines[product], 10m, lotNumber: "RCV-1"));
        Assert.True(status == HttpStatusCode.OK, body.ToString());

        Assert.Equal("RECEIVED", await Api.OrderStatus(order));
        var stock = await Api.Stock(product);
        Assert.Equal(10m, stock.GetProperty("availableQuantity").GetDecimal());
        var lot = Assert.Single(stock.GetProperty("lots").EnumerateArray());
        Assert.Equal("RCV-1", lot.GetProperty("lotNumber").GetString());
        Assert.Equal(4.5m, lot.GetProperty("unitCost").GetDecimal());

        var journal = await Api.WaitForJournal($"goods-received:{order}");
        ErpApi.AssertBalanced(journal);
        var accounts = ErpApi.Accounts(journal);
        Assert.Equal(45m, accounts["INVENTORY"].Debit);
        Assert.Equal(45m, accounts["GOODS_RECEIVED_NOT_INVOICED"].Credit);
    }

    [Fact]
    public async Task An_order_may_list_the_same_product_on_two_lines_and_both_become_lots()
    {
        var product = await Api.CreateProduct();
        var order = await Api.CreateOrder(await Api.CreateSupplier(), (product, 5m, 2m), (product, 3m, 2.5m));
        await Api.PlaceOrder(order);
        var lines = (await Api.Order(order)).GetProperty("lines").EnumerateArray()
            .Select(l => (Id: l.GetProperty("id").GetGuid(), Quantity: decimal.Parse(l.GetProperty("quantity").GetString()!, System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();

        var (status, body) = await Api.Receive(order, lines.Select(l => ErpApi.ReceiptLine(l.Id, l.Quantity)).ToArray());

        Assert.True(status == HttpStatusCode.OK, body.ToString());
        var stock = await Api.Stock(product);
        Assert.Equal(8m, stock.GetProperty("availableQuantity").GetDecimal());
        Assert.Equal(2, stock.GetProperty("lots").GetArrayLength());
        Assert.Equal(1, await StockLevels(product)); // one stock level per product
    }

    [Fact]
    public async Task A_draft_order_cannot_be_received()
    {
        var product = await Api.CreateProduct();
        var order = await Api.CreateOrder(await Api.CreateSupplier(), (product, 5m, 2m));
        var lines = await Api.OrderLineIdsByProduct(order);

        var (status, _) = await Api.Receive(order, ErpApi.ReceiptLine(lines[product], 5m));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("DRAFT", await Api.OrderStatus(order));
        Assert.Equal(0, await StockLevels(product));
    }

    [Fact]
    public async Task A_receipt_must_cover_every_order_line()
    {
        var first = await Api.CreateProduct();
        var second = await Api.CreateProduct();
        var order = await Api.CreateOrder(await Api.CreateSupplier(), (first, 5m, 2m), (second, 3m, 1m));
        await Api.PlaceOrder(order);
        var lines = await Api.OrderLineIdsByProduct(order);

        var (status, _) = await Api.Receive(order, ErpApi.ReceiptLine(lines[first], 5m));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("PLACED", await Api.OrderStatus(order));
        Assert.Equal(0, await StockLevels(first));
        Assert.Equal(0, await StockLevels(second));
    }

    [Fact]
    public async Task A_receipt_cannot_repeat_a_line()
    {
        var product = await Api.CreateProduct();
        var order = await Api.CreateOrder(await Api.CreateSupplier(), (product, 5m, 2m));
        await Api.PlaceOrder(order);
        var line = (await Api.OrderLineIdsByProduct(order))[product];

        var (status, _) = await Api.Receive(order, ErpApi.ReceiptLine(line, 5m), ErpApi.ReceiptLine(line, 5m));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("PLACED", await Api.OrderStatus(order));
    }

    [Fact]
    public async Task Received_quantity_must_match_the_order()
    {
        var product = await Api.CreateProduct();
        var order = await Api.CreateOrder(await Api.CreateSupplier(), (product, 5m, 2m));
        await Api.PlaceOrder(order);
        var line = (await Api.OrderLineIdsByProduct(order))[product];

        var (status, _) = await Api.Receive(order, ErpApi.ReceiptLine(line, 4m));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("PLACED", await Api.OrderStatus(order));
        Assert.Equal(0, await StockLevels(product));
    }

    [Fact]
    public async Task A_perishable_line_without_an_expiry_date_rolls_back_the_whole_receipt()
    {
        var plain = await Api.CreateProduct();
        var perishable = await Api.CreateProduct(perishable: true);
        var order = await Api.CreateOrder(await Api.CreateSupplier(), (plain, 5m, 2m), (perishable, 3m, 1m));
        await Api.PlaceOrder(order);
        var lines = await Api.OrderLineIdsByProduct(order);

        var (status, _) = await Api.Receive(order,
            ErpApi.ReceiptLine(lines[plain], 5m),
            ErpApi.ReceiptLine(lines[perishable], 3m));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("PLACED", await Api.OrderStatus(order));
        Assert.Equal(0, await StockLevels(plain)); // the valid line must not leak stock
        Assert.Equal(0, await StockLevels(perishable));
        Assert.Equal(0, await fixture.ScalarAsync<int>(
            "SELECT COUNT(*) FROM platform.dotnet_outbox WHERE payload LIKE @p", ("p", $"%{order}%")));
    }

    [Fact]
    public async Task Receiving_twice_adds_stock_and_posts_the_journal_only_once()
    {
        var product = await Api.CreateProduct();
        var order = await Api.CreateOrder(await Api.CreateSupplier(), (product, 10m, 4m));
        await Api.PlaceOrder(order);
        var line = (await Api.OrderLineIdsByProduct(order))[product];

        Assert.Equal(HttpStatusCode.OK, (await Api.Receive(order, ErpApi.ReceiptLine(line, 10m))).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Api.Receive(order, ErpApi.ReceiptLine(line, 10m))).Status);

        await Api.WaitForJournal($"goods-received:{order}");
        await Api.WaitForOutboxDrained(order);
        Assert.Equal(10m, await Api.Available(product));
        Assert.Equal(1, await fixture.ScalarAsync<int>(
            "SELECT COUNT(*) FROM finance.journal_entry WHERE event_key = @k", ("k", $"goods-received:{order}")));
    }

    [Fact]
    public async Task A_cancelled_order_cannot_be_placed_or_received()
    {
        var product = await Api.CreateProduct();
        var order = await Api.CreateOrder(await Api.CreateSupplier(), (product, 5m, 2m));
        await Api.PostOk($"/api/procurement/orders/{order}/cancel");
        var line = (await Api.OrderLineIdsByProduct(order))[product];

        Assert.Equal(HttpStatusCode.Conflict, (await Api.Post($"/api/procurement/orders/{order}/place")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Api.Receive(order, ErpApi.ReceiptLine(line, 5m))).Status);
        Assert.Equal("CANCELLED", await Api.OrderStatus(order));
    }

    [Fact]
    public async Task Direct_stock_receipt_validates_unit_and_expiry()
    {
        var each = await Api.CreateProduct("EACH");
        var perishable = await Api.CreateProduct(perishable: true);

        var wrongUnit = await Api.Post("/api/inventory/receive-stock",
            new { productId = each, quantityValue = 1, unitType = "WEIGHT", unitCost = 1 });
        var fractionalEach = await Api.Post("/api/inventory/receive-stock",
            new { productId = each, quantityValue = 1.5, unitType = "EACH", unitCost = 1 });
        var noExpiry = await Api.Post("/api/inventory/receive-stock",
            new { productId = perishable, quantityValue = 1, unitType = "EACH", unitCost = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, wrongUnit.Status);
        Assert.Equal(HttpStatusCode.BadRequest, fractionalEach.Status);
        Assert.Equal(HttpStatusCode.BadRequest, noExpiry.Status);
        Assert.Equal(0, await StockLevels(each) + await StockLevels(perishable));
    }

    private Task<int> StockLevels(Guid productId) => fixture.ScalarAsync<int>(
        "SELECT COUNT(*) FROM inventory.stock_level WHERE product_id = @p", ("p", productId));
}
