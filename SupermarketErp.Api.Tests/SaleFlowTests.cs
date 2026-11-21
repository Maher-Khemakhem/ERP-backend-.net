using System.Net;

namespace SupermarketErp.Api.Tests;

/// <summary>
/// Characterization tests for the in-store sale flow:
/// price a ticket -> sell stock (FEFO) -> complete -> outbox event -> balanced journal entry.
/// They pin down today's behaviour at the HTTP boundary so later refactors can be verified.
/// </summary>
public sealed class SaleFlowTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private ErpApi Api => fixture.Api;

    [Fact]
    public async Task Cash_sale_deducts_stock_updates_drawer_and_posts_a_balanced_journal()
    {
        // price 10.000 x 2 = 20.000 net, 19% VAT = 3.800, gross 23.800; stock cost 4.000 x 2 = 8.000
        var product = await Api.ProductWithStock(price: 10m, quantity: 10, unitCost: 4m);
        var session = await Api.OpenSession(openingFloat: 50m);
        var transaction = await Api.StartTransaction(session);
        var line = await Api.AddLine(transaction, product, 2);

        Assert.Equal(23.8m, ErpApi.Money(line.GetProperty("linePrice")));
        Assert.Equal(20m, ErpApi.Money(line.GetProperty("netPrice")));
        Assert.Equal(3.8m, ErpApi.Money(line.GetProperty("vatAmount")));

        var (status, body) = await Api.Complete(transaction, "CASH", amountReceived: 25m);
        Assert.True(status == HttpStatusCode.OK, body.ToString());

        var ticket = await Api.Transaction(transaction);
        Assert.Equal("COMPLETED", ticket.GetProperty("status").GetString());
        Assert.Equal("CASH", ticket.GetProperty("paymentMethod").GetString());
        Assert.Equal(8m, await Api.Available(product));
        Assert.Equal(73.8m, await Api.ExpectedCash(session)); // opening float + cash taken

        var journal = await Api.WaitForJournal($"sale:{transaction}");
        ErpApi.AssertBalanced(journal);
        var accounts = ErpApi.Accounts(journal);
        Assert.Equal(23.8m, accounts["CASH_ON_HAND"].Debit);
        Assert.Equal(20m, accounts["SALES_REVENUE"].Credit);
        Assert.Equal(3.8m, accounts["OUTPUT_VAT"].Credit);
        Assert.Equal(8m, accounts["COST_OF_GOODS_SOLD"].Debit);
        Assert.Equal(8m, accounts["INVENTORY"].Credit);

        await Api.WaitForOutboxDrained(transaction);
        Assert.Equal(0, await fixture.ScalarAsync<int>(
            "SELECT COUNT(*) FROM platform.dotnet_outbox WHERE payload LIKE @p AND last_error IS NOT NULL",
            ("p", $"%{transaction}%")));
    }

    [Fact]
    public async Task Card_sale_posts_to_card_clearing_and_leaves_the_drawer_untouched()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 10, unitCost: 4m);
        var session = await Api.OpenSession(openingFloat: 50m);
        var transaction = await Api.StartTransaction(session);
        await Api.AddLine(transaction, product, 2);

        var (status, body) = await Api.Complete(transaction, "CARD", amountReceived: 23.8m);
        Assert.True(status == HttpStatusCode.OK, body.ToString());

        Assert.Equal(50m, await Api.ExpectedCash(session));
        var accounts = ErpApi.Accounts(await Api.WaitForJournal($"sale:{transaction}"));
        Assert.Equal(23.8m, accounts["CARD_CLEARING"].Debit);
        Assert.False(accounts.ContainsKey("CASH_ON_HAND"));
    }

    [Fact]
    public async Task Stock_is_sold_first_expired_first_out_and_cost_of_goods_follows_the_lots_used()
    {
        var product = await Api.CreateProduct(perishable: true);
        await Api.SetPrice(product, 10m);
        var today = DateOnly.FromDateTime(DateTime.Today);
        await Api.ReceiveStock(product, 5, unitCost: 5m, lotNumber: "LATE", expiry: today.AddDays(30));
        await Api.ReceiveStock(product, 2, unitCost: 3m, lotNumber: "EARLY", expiry: today.AddDays(5));

        var session = await Api.OpenSession();
        var transaction = await Api.StartTransaction(session);
        await Api.AddLine(transaction, product, 3);
        var (status, body) = await Api.Complete(transaction, "CASH", 100m);
        Assert.True(status == HttpStatusCode.OK, body.ToString());

        var lots = (await Api.Stock(product)).GetProperty("lots").EnumerateArray()
            .ToDictionary(l => l.GetProperty("lotNumber").GetString()!, l => l.GetProperty("quantity").GetDecimal());
        Assert.DoesNotContain("EARLY", lots.Keys); // emptied lots are not listed
        Assert.Equal(4m, lots["LATE"]);

        // 2 x 3.000 from the early lot + 1 x 5.000 from the late lot
        var accounts = ErpApi.Accounts(await Api.WaitForJournal($"sale:{transaction}"));
        Assert.Equal(11m, accounts["COST_OF_GOODS_SOLD"].Debit);
    }

    [Fact]
    public async Task Insufficient_stock_rejects_the_sale_and_changes_nothing()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 1, unitCost: 4m);
        var session = await Api.OpenSession(openingFloat: 50m);
        var transaction = await Api.StartTransaction(session);
        await Api.AddLine(transaction, product, 2);

        var (status, _) = await Api.Complete(transaction, "CASH", 100m);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("IN_PROGRESS", (await Api.Transaction(transaction)).GetProperty("status").GetString());
        Assert.Equal(1m, await Api.Available(product));
        Assert.Equal(50m, await Api.ExpectedCash(session));
        Assert.Equal(0, await OutboxRowsMentioning(transaction));
    }

    [Fact]
    public async Task A_failing_second_line_rolls_back_stock_already_deducted_for_the_first_line()
    {
        // The first product is below its reorder point after the sale, which makes the sale flush
        // changes and write an outbox event mid-transaction: the strictest rollback scenario.
        var plentiful = await Api.ProductWithStock(price: 10m, quantity: 10, unitCost: 4m, reorderPoint: 100);
        var scarce = await Api.ProductWithStock(price: 10m, quantity: 1, unitCost: 4m);
        var session = await Api.OpenSession(openingFloat: 50m);
        var transaction = await Api.StartTransaction(session);
        await Api.AddLine(transaction, plentiful, 3);
        await Api.AddLine(transaction, scarce, 2);

        var (status, _) = await Api.Complete(transaction, "CASH", 500m);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(10m, await Api.Available(plentiful));
        Assert.Equal(1m, await Api.Available(scarce));
        Assert.Equal(50m, await Api.ExpectedCash(session));
        Assert.Equal("IN_PROGRESS", (await Api.Transaction(transaction)).GetProperty("status").GetString());
        Assert.Equal(0, await fixture.ScalarAsync<int>(
            "SELECT COUNT(*) FROM platform.dotnet_outbox WHERE payload LIKE @p", ("p", $"%{plentiful}%")));
    }

    [Fact]
    public async Task Cash_tendered_below_the_total_is_rejected()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 10, unitCost: 4m);
        var session = await Api.OpenSession();
        var transaction = await Api.StartTransaction(session);
        await Api.AddLine(transaction, product, 2); // total 23.800

        var (status, _) = await Api.Complete(transaction, "CASH", amountReceived: 20m);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("IN_PROGRESS", (await Api.Transaction(transaction)).GetProperty("status").GetString());
        Assert.Equal(10m, await Api.Available(product));
    }

    [Fact]
    public async Task Completing_a_ticket_twice_sells_and_posts_only_once()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 10, unitCost: 4m);
        var session = await Api.OpenSession();
        var transaction = await Api.StartTransaction(session);
        await Api.AddLine(transaction, product, 2);

        Assert.Equal(HttpStatusCode.OK, (await Api.Complete(transaction, "CASH", 100m)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Api.Complete(transaction, "CASH", 100m)).Status);

        await Api.WaitForJournal($"sale:{transaction}");
        await Api.WaitForOutboxDrained(transaction);
        Assert.Equal(8m, await Api.Available(product));
        Assert.Equal(1, await fixture.ScalarAsync<int>(
            "SELECT COUNT(*) FROM finance.journal_entry WHERE event_key = @k", ("k", $"sale:{transaction}")));
    }

    [Fact]
    public async Task Voided_ticket_does_not_touch_stock_or_the_books()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 10, unitCost: 4m);
        var session = await Api.OpenSession();
        var transaction = await Api.StartTransaction(session);
        await Api.AddLine(transaction, product, 2);

        await Api.PostOk($"/api/pos/transactions/{transaction}/void", new { reason = "customer left" });

        Assert.Equal("VOIDED", (await Api.Transaction(transaction)).GetProperty("status").GetString());
        Assert.Equal(10m, await Api.Available(product));
        Assert.Equal(0, await OutboxRowsMentioning(transaction));
    }

    [Fact]
    public async Task Closing_a_session_posts_the_cash_deposit_and_the_shortage()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 10, unitCost: 4m);
        var session = await Api.OpenSession(openingFloat: 50m);
        var transaction = await Api.StartTransaction(session);
        await Api.AddLine(transaction, product, 2);
        await Api.Complete(transaction, "CASH", 100m); // expected cash 50 + 23.8 = 73.8

        var (status, body) = await Api.CloseSession(session, countedCash: 70m);
        Assert.True(status == HttpStatusCode.OK, body.ToString());

        var journal = await Api.WaitForJournal($"cash-close:{session}");
        ErpApi.AssertBalanced(journal);
        var accounts = ErpApi.Accounts(journal);
        Assert.Equal(70m, accounts["BANK"].Debit);
        Assert.Equal(73.8m, accounts["CASH_ON_HAND"].Credit);
        Assert.Equal(3.8m, accounts["CASH_VARIANCE"].Debit); // shortage

        // a closed session cannot take new tickets
        var (restartStatus, _) = await Api.Post("/api/pos/transactions/start", new { cashSessionId = session });
        Assert.Equal(HttpStatusCode.Conflict, restartStatus);
    }

    [Fact]
    public async Task Closing_a_session_with_surplus_cash_posts_an_overage()
    {
        var session = await Api.OpenSession(openingFloat: 50m);

        await Api.CloseSession(session, countedCash: 52m);

        var accounts = ErpApi.Accounts(await Api.WaitForJournal($"cash-close:{session}"));
        Assert.Equal(52m, accounts["BANK"].Debit);
        Assert.Equal(50m, accounts["CASH_ON_HAND"].Credit);
        Assert.Equal(2m, accounts["CASH_VARIANCE"].Credit);
    }

    private Task<int> OutboxRowsMentioning(Guid id) => fixture.ScalarAsync<int>(
        "SELECT COUNT(*) FROM platform.dotnet_outbox WHERE payload LIKE @p", ("p", $"%{id}%"));
}
