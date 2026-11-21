namespace SupermarketErp.Api.Tests;

/// <summary>
/// The transactional outbox and the low-stock alerts it feeds.
/// </summary>
public sealed class OutboxTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private ErpApi Api => fixture.Api;

    [Fact]
    public async Task Selling_while_at_or_below_the_reorder_point_raises_a_low_stock_alert()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 6, unitCost: 4m, reorderPoint: 5);

        await SellOne(product, quantity: 2); // 6 -> 4
        await SellOne(product);              // 4 -> 3, sold while already below the reorder point

        var alerts = await ErpApi.Eventually(() => Api.Alerts(product), found => found.Length > 0, "a low-stock alert");
        Assert.False(alerts[0].GetProperty("acknowledged").GetBoolean());
    }

    [Fact]
    public async Task The_sale_that_crosses_the_reorder_point_raises_the_alert()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 6, unitCost: 4m, reorderPoint: 5);

        var transaction = await SellOne(product, quantity: 2); // 6 -> 4: crosses the reorder point of 5
        await Api.WaitForJournal($"sale:{transaction}");
        await Api.WaitForOutboxDrained(transaction);

        Assert.NotEmpty(await Api.Alerts(product));
    }

    [Fact]
    public async Task Selling_above_the_reorder_point_raises_no_alert()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 20, unitCost: 4m, reorderPoint: 5);

        var transaction = await SellOne(product);
        await Api.WaitForJournal($"sale:{transaction}"); // outbox is ordered, so alerts would be handled by now

        Assert.Empty(await Api.Alerts(product));
    }

    [Fact]
    public async Task Repeated_sales_below_the_reorder_point_keep_a_single_open_alert()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 20, unitCost: 4m, reorderPoint: 5);

        await SellOne(product, quantity: 16); // 4 left: below the reorder point
        await SellOne(product);               // 3 left
        var last = await SellOne(product);    // 2 left
        await Api.WaitForJournal($"sale:{last}");
        await Api.WaitForOutboxDrained(last);

        var open = (await Api.Alerts(product)).Where(a => !a.GetProperty("acknowledged").GetBoolean());
        Assert.Single(open);
    }

    [Fact]
    public async Task An_acknowledged_alert_is_replaced_by_a_new_one_when_stock_is_still_low()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 20, unitCost: 4m, reorderPoint: 5);
        var first = await SellOne(product, quantity: 16); // 4 left, crossing the reorder point raises the alert
        await Api.WaitForOutboxDrained(first);
        var original = Assert.Single(await Api.Alerts(product));
        await Api.PostOk($"/api/procurement/alerts/{original.GetProperty("id").GetGuid()}/acknowledge");

        var second = await SellOne(product); // still low, nothing open: raise a fresh alert
        await Api.WaitForOutboxDrained(second);

        var alerts = await Api.Alerts(product);
        Assert.Equal(2, alerts.Length);
        Assert.Single(alerts, a => !a.GetProperty("acknowledged").GetBoolean());
    }

    private async Task<Guid> SellOne(Guid product, decimal quantity = 1)
    {
        var session = await Api.OpenSession();
        var transaction = await Api.StartTransaction(session);
        await Api.AddLine(transaction, product, quantity);
        var (status, body) = await Api.Complete(transaction, "CARD", amountReceived: null);
        Assert.True(status == System.Net.HttpStatusCode.OK, body.ToString());
        return transaction;
    }
}
