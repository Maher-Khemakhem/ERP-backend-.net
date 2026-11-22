using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SupermarketErp.Api.Tests;

/// <summary>
/// Characterization tests for accounting: supplier invoices (VAT, payable), supplier payments (partial, full,
/// idempotent), card settlement, the profit-and-loss report and who may see the books.
/// This class owns its database, so the report totals are exact.
/// </summary>
public sealed class FinanceAccountingTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private ErpApi Api => fixture.Api;

    // ---- supplier invoices -----------------------------------------------------------------

    [Fact]
    public async Task An_invoice_adds_vat_and_posts_goods_received_input_vat_and_payable()
    {
        var supplier = await Api.CreateSupplier();

        var invoice = await CreateInvoice(supplier, "INV-" + Guid.NewGuid().ToString("N")[..8], 100m, vatRate: 19m);

        Assert.Equal("100.000 TND", invoice.GetProperty("subtotal").GetString());
        Assert.Equal("19.000 TND", invoice.GetProperty("taxAmount").GetString());
        Assert.Equal("119.000 TND", invoice.GetProperty("total").GetString());
        Assert.Equal("0.000 TND", invoice.GetProperty("paidAmount").GetString());
        Assert.Equal("119.000 TND", invoice.GetProperty("outstanding").GetString());
        Assert.Equal("OPEN", invoice.GetProperty("status").GetString());

        var journal = await Api.WaitForJournal($"supplier-invoice:{invoice.GetProperty("id").GetGuid()}");
        ErpApi.AssertBalanced(journal);
        var accounts = ErpApi.Accounts(journal);
        Assert.Equal(100m, accounts["GOODS_RECEIVED_NOT_INVOICED"].Debit);
        Assert.Equal(19m, accounts["INPUT_VAT"].Debit);
        Assert.Equal(119m, accounts["ACCOUNTS_PAYABLE"].Credit);
        Assert.Contains((await Api.GetOk("/api/finance/supplier-invoices")).EnumerateArray(),
            i => i.GetProperty("id").GetGuid() == invoice.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task An_invoice_without_vat_has_no_input_vat_line()
    {
        var invoice = await CreateInvoice(await Api.CreateSupplier(), "INV-" + Guid.NewGuid().ToString("N")[..8], 80.5m, vatRate: 0m);

        Assert.Equal("80.500 TND", invoice.GetProperty("total").GetString());
        var accounts = ErpApi.Accounts(await Api.WaitForJournal($"supplier-invoice:{invoice.GetProperty("id").GetGuid()}"));
        Assert.False(accounts.ContainsKey("INPUT_VAT"));
        Assert.Equal(80.5m, accounts["ACCOUNTS_PAYABLE"].Credit);
    }

    [Fact]
    public async Task Invoice_input_is_validated_and_numbers_are_unique()
    {
        var supplier = await Api.CreateSupplier();
        var number = "INV-" + Guid.NewGuid().ToString("N")[..8];
        var today = DateOnly.FromDateTime(DateTime.Today);

        Assert.Equal(HttpStatusCode.BadRequest, (await TryInvoice(Guid.Empty, number, 10m, 19m)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await TryInvoice(supplier, "  ", 10m, 19m)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await TryInvoice(supplier, number, 0m, 19m)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await TryInvoice(supplier, number, 10m, -1m)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await TryInvoice(supplier, number, 10m, 101m)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await TryInvoice(supplier, number, 10m, 19m, issue: today, due: today.AddDays(-1))).Status);

        Assert.Equal(HttpStatusCode.OK, (await TryInvoice(supplier, number, 10m, 19m)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await TryInvoice(supplier, number, 10m, 19m)).Status); // same invoice number
    }

    // ---- supplier payments -----------------------------------------------------------------

    [Fact]
    public async Task An_invoice_is_paid_in_parts_until_it_is_settled()
    {
        var invoice = await CreateInvoice(await Api.CreateSupplier(), "INV-" + Guid.NewGuid().ToString("N")[..8], 100m, 19m); // 119.000
        var id = invoice.GetProperty("id").GetGuid();
        var first = "PAY-" + Guid.NewGuid().ToString("N")[..8];
        var second = "PAY-" + Guid.NewGuid().ToString("N")[..8];

        var partial = await Pay(id, first, 50m, "bank_transfer");
        Assert.Equal("PARTIALLY_PAID", partial.GetProperty("status").GetString());
        Assert.Equal("50.000 TND", partial.GetProperty("paidAmount").GetString());
        Assert.Equal("69.000 TND", partial.GetProperty("outstanding").GetString());
        var firstJournal = ErpApi.Accounts(await Api.WaitForJournal($"supplier-payment:{first}"));
        Assert.Equal(50m, firstJournal["ACCOUNTS_PAYABLE"].Debit);
        Assert.Equal(50m, firstJournal["BANK"].Credit);

        var settled = await Pay(id, second, 69m, "cash");
        Assert.Equal("PAID", settled.GetProperty("status").GetString());
        Assert.Equal("0.000 TND", settled.GetProperty("outstanding").GetString());
        var secondJournal = ErpApi.Accounts(await Api.WaitForJournal($"supplier-payment:{second}"));
        Assert.Equal(69m, secondJournal["CASH_ON_HAND"].Credit); // paid in cash: out of the drawer, not the bank

        Assert.Equal(HttpStatusCode.Conflict, (await TryPay(id, "PAY-" + Guid.NewGuid().ToString("N")[..8], 1m, "CASH")).Status); // already paid
    }

    [Fact]
    public async Task A_payment_cannot_exceed_the_balance_and_must_be_complete()
    {
        var invoice = await CreateInvoice(await Api.CreateSupplier(), "INV-" + Guid.NewGuid().ToString("N")[..8], 100m, 0m);
        var id = invoice.GetProperty("id").GetGuid();
        var reference = "PAY-" + Guid.NewGuid().ToString("N")[..8];

        Assert.Equal(HttpStatusCode.BadRequest, (await TryPay(id, reference, 100.001m, "CASH")).Status); // more than owed
        Assert.Equal(HttpStatusCode.BadRequest, (await TryPay(id, " ", 10m, "CASH")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await TryPay(id, reference, 0m, "CASH")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await TryPay(id, reference, 10m, " ")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await TryPay(Guid.NewGuid(), reference, 10m, "CASH")).Status); // unknown invoice
        Assert.Equal("OPEN", (await InvoiceById(id)).GetProperty("status").GetString());
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM finance.supplier_payment WHERE invoice_id = @i", ("i", id)));
    }

    [Fact]
    public async Task Repeating_a_payment_reference_does_not_pay_twice()
    {
        var invoice = await CreateInvoice(await Api.CreateSupplier(), "INV-" + Guid.NewGuid().ToString("N")[..8], 100m, 0m);
        var other = await CreateInvoice(await Api.CreateSupplier(), "INV-" + Guid.NewGuid().ToString("N")[..8], 100m, 0m);
        var id = invoice.GetProperty("id").GetGuid();
        var reference = "PAY-" + Guid.NewGuid().ToString("N")[..8];
        await Pay(id, reference, 40m, "BANK");

        var again = await Pay(id, reference, 40m, "BANK"); // a retried request

        Assert.Equal("40.000 TND", again.GetProperty("paidAmount").GetString());
        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM finance.supplier_payment WHERE payment_reference = @r", ("r", reference)));
        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM finance.journal_entry WHERE event_key = @k", ("k", $"supplier-payment:{reference}")));
        Assert.Equal(HttpStatusCode.BadRequest, (await TryPay(other.GetProperty("id").GetGuid(), reference, 40m, "BANK")).Status); // belongs to another invoice
    }

    // ---- card settlement -------------------------------------------------------------------

    [Fact]
    public async Task A_card_settlement_moves_money_from_card_clearing_to_the_bank_once()
    {
        var reference = "SETTLE-" + Guid.NewGuid().ToString("N")[..8];

        await Api.PostOk("/api/finance/settlements/card", new { settlementReference = reference, amount = 250.5m });
        await Api.PostOk("/api/finance/settlements/card", new { settlementReference = reference, amount = 250.5m }); // retry

        var journal = await Api.WaitForJournal($"card-settlement:{reference}");
        ErpApi.AssertBalanced(journal);
        var accounts = ErpApi.Accounts(journal);
        Assert.Equal(250.5m, accounts["BANK"].Debit);
        Assert.Equal(250.5m, accounts["CARD_CLEARING"].Credit);
        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM finance.journal_entry WHERE event_key = @k", ("k", $"card-settlement:{reference}")));
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Post("/api/finance/settlements/card", new { settlementReference = " ", amount = 10m })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Post("/api/finance/settlements/card", new { settlementReference = "X", amount = 0m })).Status);
    }

    // ---- reports ---------------------------------------------------------------------------

    [Fact]
    public async Task The_profit_and_loss_report_adds_up_the_journals_of_the_period()
    {
        // price 10 x 2 = 20 net revenue, 3.8 VAT, cost 4 x 2 = 8; the drawer is 3.8 short at close
        var product = await Api.ProductWithStock(price: 10m, quantity: 10, unitCost: 4m);
        var session = await Api.OpenSession(openingFloat: 50m);
        var ticket = await Api.StartTransaction(session);
        await Api.AddLine(ticket, product, 2);
        await Api.Complete(ticket, "CASH", 100m);
        await Api.CloseSession(session, 70m);
        await Api.WaitForJournal($"sale:{ticket}");
        await Api.WaitForJournal($"cash-close:{session}");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var report = await Api.GetOk($"/api/finance/reports/profit-and-loss?from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}");

        Assert.Equal("20.000 TND", report.GetProperty("revenue").GetString());
        Assert.Equal("3.800 TND", report.GetProperty("outputVat").GetString());
        Assert.Equal("8.000 TND", report.GetProperty("costOfGoodsSold").GetString());
        Assert.Equal("12.000 TND", report.GetProperty("grossProfit").GetString());
        Assert.Equal("-3.800 TND", report.GetProperty("cashVariance").GetString());
        Assert.Equal("8.200 TND", report.GetProperty("netProfit").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Send(HttpMethod.Get, "/api/finance/reports/profit-and-loss?from=2026-03-02&to=2026-03-01")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Api.Send(HttpMethod.Get, "/api/finance/reports/profit-and-loss")).Status); // defaults to this month
    }

    [Fact]
    public async Task The_ledger_lists_the_lines_of_every_journal()
    {
        var reference = "SETTLE-" + Guid.NewGuid().ToString("N")[..8];
        await Api.PostOk("/api/finance/settlements/card", new { settlementReference = reference, amount = 12m });
        var journal = await Api.WaitForJournal($"card-settlement:{reference}");

        var ledger = (await Api.GetOk("/api/finance/ledger")).EnumerateArray().Where(e => e.GetProperty("description").GetString()!.Contains(reference)).ToArray();

        Assert.Equal(2, ledger.Length);
        Assert.Contains(ledger, e => e.GetProperty("entryType").GetString() == "BANK" && e.GetProperty("amount").GetString() == "12.000 TND");
        Assert.Contains(ledger, e => e.GetProperty("entryType").GetString() == "CARD_CLEARING");
        Assert.Equal(2, journal.GetProperty("lines").GetArrayLength());
    }

    // ---- who may see the books -------------------------------------------------------------

    [Fact]
    public async Task Only_admins_managers_and_accountants_can_use_finance()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send("ACCOUNTANT", "/api/finance/journals")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send("MANAGER", "/api/finance/journals")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send("CASHIER", "/api/finance/journals")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send("INVENTORY_MANAGER", "/api/finance/supplier-invoices")).StatusCode);
        using var anonymous = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/finance/journals")).StatusCode);
    }

    [Fact]
    public async Task Accountants_can_read_purchase_orders_but_cashiers_cannot()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send("ACCOUNTANT", "/api/procurement/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send("INVENTORY_MANAGER", "/api/procurement/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send("CASHIER", "/api/procurement/orders")).StatusCode);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private async Task<(HttpStatusCode Status, JsonElement Body)> TryInvoice(Guid supplier, string number, decimal subtotal, decimal vat,
        DateOnly? issue = null, DateOnly? due = null) =>
        await Api.Post("/api/finance/supplier-invoices", new
        {
            supplierId = supplier, invoiceNumber = number, subtotal, vatRate = vat, issueDate = issue, dueDate = due
        });

    private async Task<JsonElement> CreateInvoice(Guid supplier, string number, decimal subtotal, decimal vatRate)
    {
        var (status, body) = await TryInvoice(supplier, number, subtotal, vatRate);
        Assert.True(status == HttpStatusCode.OK, body.ToString());
        return body;
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> TryPay(Guid invoice, string reference, decimal amount, string method) =>
        Api.Post($"/api/finance/supplier-invoices/{invoice}/payments", new { paymentReference = reference, amount, paymentMethod = method });

    private async Task<JsonElement> Pay(Guid invoice, string reference, decimal amount, string method)
    {
        var (status, body) = await TryPay(invoice, reference, amount, method);
        Assert.True(status == HttpStatusCode.OK, body.ToString());
        return body;
    }

    private async Task<JsonElement> InvoiceById(Guid id) =>
        (await Api.GetOk("/api/finance/supplier-invoices")).EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id);

    /// <summary>GET as a user with the given role (created on demand by the admin).</summary>
    private async Task<HttpResponseMessage> Send(string role, string url)
    {
        var username = "u" + role.ToLowerInvariant().Replace("_", "") + Guid.NewGuid().ToString("N")[..6];
        await Api.PostOk("/api/auth/users", new { username, displayName = username, password = "Str0ng-Test-Passw0rd!", roles = new[] { role } });
        using var anonymous = fixture.Factory.CreateClient();
        var login = await anonymous.PostAsJsonAsync("/api/auth/login", new { username, password = "Str0ng-Test-Passw0rd!" });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await fixture.Factory.CreateClient().SendAsync(request);
    }
}
