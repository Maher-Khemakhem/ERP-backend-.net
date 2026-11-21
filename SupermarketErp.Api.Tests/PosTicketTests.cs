using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SupermarketErp.Api.Tests;

/// <summary>
/// Characterization tests for the rest of the POS surface: tills and sessions, ticket editing (lines, void,
/// delete), validation, weighed items and the phone scanner. SaleFlowTests covers the money flow.
/// </summary>
public sealed class PosTicketTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private ErpApi Api => fixture.Api;

    // ---- tills and sessions ----------------------------------------------------------------

    [Fact]
    public async Task A_till_shows_its_cashier_and_the_cash_in_its_drawer_through_a_session()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 10, unitCost: 4m);
        var till = (await Api.PostOk("/api/pos/tills", new { tillNumber = "  Till " + Guid.NewGuid().ToString("N")[..6] + "  " }))
            .GetProperty("id").GetGuid();

        var closed = await Api.GetOk($"/api/pos/tills/{till}");
        Assert.Equal("CLOSED", closed.GetProperty("status").GetString());
        Assert.StartsWith("Till ", closed.GetProperty("tillNumber").GetString()); // trimmed
        Assert.Equal("0.000 TND", closed.GetProperty("cashInDrawer").GetString());
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("cashierName").ValueKind);

        var session = (await Api.PostOk("/api/pos/sessions/open", new { tillId = till, cashierName = "  Amel  ", openingFloat = 50m }))
            .GetProperty("cashSessionId").GetGuid();
        var open = await Api.GetOk($"/api/pos/tills/{till}");
        Assert.Equal("OPEN", open.GetProperty("status").GetString());
        Assert.Equal("Amel", open.GetProperty("cashierName").GetString());
        Assert.Equal("50.000 TND", open.GetProperty("cashInDrawer").GetString());

        var ticket = await Api.StartTransaction(session);
        await Api.AddLine(ticket, product, 2);
        await Api.Complete(ticket, "CASH", 100m);
        Assert.Equal("73.800 TND", (await Api.GetOk($"/api/pos/tills/{till}")).GetProperty("cashInDrawer").GetString());

        await Api.CloseSession(session, 70m);
        var after = await Api.GetOk($"/api/pos/tills/{till}");
        Assert.Equal("CLOSED", after.GetProperty("status").GetString());
        Assert.Equal("70.000 TND", after.GetProperty("cashInDrawer").GetString()); // what was counted
        Assert.Contains((await Api.GetOk("/api/pos/tills")).EnumerateArray(), t => t.GetProperty("id").GetGuid() == till);
    }

    [Fact]
    public async Task The_active_session_of_a_till_is_found_while_it_is_open()
    {
        var till = (await Api.PostOk("/api/pos/tills", new { tillNumber = "T-" + Guid.NewGuid().ToString("N")[..8] })).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await Api.Send(HttpMethod.Get, $"/api/pos/sessions/active?tillId={till}")).Status);

        var session = (await Api.PostOk("/api/pos/sessions/open", new { tillId = till, cashierName = "A", openingFloat = 10m }))
            .GetProperty("cashSessionId").GetGuid();

        var active = await Api.GetOk($"/api/pos/sessions/active?tillId={till}");
        Assert.Equal(session, active.GetProperty("id").GetGuid());
        Assert.Equal("OPEN", active.GetProperty("status").GetString());
        Assert.Equal("10.000 TND", active.GetProperty("expectedCash").GetString());

        await Api.CloseSession(session, 10m);
        Assert.Equal(HttpStatusCode.NoContent, (await Api.Send(HttpMethod.Get, $"/api/pos/sessions/active?tillId={till}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.Send(HttpMethod.Get, $"/api/pos/tills/{Guid.NewGuid()}")).Status);
    }

    [Fact]
    public async Task Till_and_session_input_is_validated()
    {
        var till = (await Api.PostOk("/api/pos/tills", new { tillNumber = "T-" + Guid.NewGuid().ToString("N")[..8] })).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Post("/api/pos/tills", new { tillNumber = "  " })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Post("/api/pos/sessions/open", new { tillId = till, cashierName = " ", openingFloat = 5m })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Post("/api/pos/sessions/open", new { tillId = till, cashierName = "A", openingFloat = -1m })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Post("/api/pos/sessions/open", new { tillId = Guid.NewGuid(), cashierName = "A", openingFloat = 5m })).Status);

        await Api.PostOk("/api/pos/sessions/open", new { tillId = till, cashierName = "A", openingFloat = 5m });
        Assert.Equal(HttpStatusCode.Conflict, (await Api.Post("/api/pos/sessions/open", new { tillId = till, cashierName = "B", openingFloat = 5m })).Status);
    }

    [Fact]
    public async Task A_session_closes_once_with_a_non_negative_count()
    {
        var session = await Api.OpenSession();

        Assert.Equal(HttpStatusCode.BadRequest, (await Api.CloseSession(session, -1m)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.CloseSession(Guid.NewGuid(), 1m)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Api.CloseSession(session, 50m)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Api.CloseSession(session, 50m)).Status);
        Assert.Equal("CLOSED", (await Api.GetOk($"/api/pos/sessions/{session}")).GetProperty("status").GetString());
    }

    // ---- ticket lines ----------------------------------------------------------------------

    [Fact]
    public async Task Lines_are_validated_when_added()
    {
        var each = await Api.ProductWithStock(price: 10m, quantity: 10, unitCost: 4m);
        var unpriced = await Api.CreateProduct();
        var ticket = await Api.StartTransaction(await Api.OpenSession());

        Assert.Equal(HttpStatusCode.BadRequest, (await AddLine(ticket, each, 0m, "EACH")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddLine(ticket, each, -1m, "EACH")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddLine(ticket, each, 1.5m, "EACH")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddLine(ticket, each, 1m, "BOX")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddLine(ticket, each, 1m, "WEIGHT")).Status); // product is sold by unit
        Assert.Equal(HttpStatusCode.BadRequest, (await AddLine(ticket, unpriced, 1m, "EACH")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddLine(ticket, Guid.NewGuid(), 1m, "EACH")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddLine(Guid.NewGuid(), each, 1m, "EACH")).Status);
        Assert.Empty((await Api.Transaction(ticket)).GetProperty("lines").EnumerateArray());
    }

    [Fact]
    public async Task A_weighed_item_is_priced_per_kilogram_and_sold_in_kilograms()
    {
        var product = await Api.CreateProduct("WEIGHT");
        await Api.SetPrice(product, 6m);
        await Api.ReceiveStock(product, 10.000m, unitCost: 2m, unit: "WEIGHT");
        var ticket = await Api.StartTransaction(await Api.OpenSession());

        var line = await Api.AddLine(ticket, product, 0.5m, "WEIGHT");
        Assert.Equal("3.570 TND", line.GetProperty("linePrice").GetString()); // 3.000 + 19% VAT
        Assert.Equal("3.000 TND", line.GetProperty("netPrice").GetString());

        var shown = (await Api.Transaction(ticket)).GetProperty("lines")[0];
        Assert.Equal("0.500 kg", shown.GetProperty("quantity").GetString());

        Assert.Equal(HttpStatusCode.OK, (await Api.Complete(ticket, "CARD", 3.57m)).Status);
        Assert.Equal(9.5m, await Api.Available(product));
        Assert.Equal(1m, ErpApi.Accounts(await Api.WaitForJournal($"sale:{ticket}"))["COST_OF_GOODS_SOLD"].Debit); // 0.5 kg x 2.000
    }

    [Fact]
    public async Task Ticket_totals_add_up_across_lines_and_lines_can_be_removed()
    {
        var first = await Api.ProductWithStock(price: 10m, quantity: 10, unitCost: 4m);
        var second = await Api.ProductWithStock(price: 5m, quantity: 10, unitCost: 2m);
        var ticket = await Api.StartTransaction(await Api.OpenSession());
        await Api.AddLine(ticket, first, 2);                       // 20.000 + 3.800
        var removable = (await Api.AddLine(ticket, second, 3)).GetProperty("lineId").GetString()!; // 15.000 + 2.850

        var both = await Api.Transaction(ticket);
        Assert.Equal("41.650 TND", both.GetProperty("total").GetString());
        Assert.Equal("35.000 TND", both.GetProperty("netTotal").GetString());
        Assert.Equal("6.650 TND", both.GetProperty("vatTotal").GetString());
        Assert.Contains((await Api.GetOk("/api/pos/transactions")).EnumerateArray(), t => t.GetProperty("id").GetGuid() == ticket);

        Assert.Equal(HttpStatusCode.OK, (await Api.Send(HttpMethod.Delete, $"/api/pos/transactions/{ticket}/lines/{removable}")).Status);
        Assert.Equal("23.800 TND", (await Api.Transaction(ticket)).GetProperty("total").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Send(HttpMethod.Delete, $"/api/pos/transactions/{ticket}/lines/{removable}")).Status); // already gone
    }

    // ---- ending a ticket -------------------------------------------------------------------

    [Fact]
    public async Task Payment_is_validated()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 10, unitCost: 4m);
        var ticket = await Api.StartTransaction(await Api.OpenSession());
        await Api.AddLine(ticket, product, 2); // 23.800

        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Complete(ticket, "CHEQUE", 30m)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Complete(ticket, "CARD", 30m)).Status);   // card must match exactly
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Complete(ticket, "CASH", 23m)).Status);   // cash must cover the total
        Assert.Equal(HttpStatusCode.OK, (await Api.Complete(ticket, "  cash ", 23.8m)).Status);      // exact cash, any case
        Assert.Equal("CASH", (await Api.Transaction(ticket)).GetProperty("paymentMethod").GetString());
    }

    [Fact]
    public async Task A_finished_ticket_can_no_longer_be_changed()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 10, unitCost: 4m);
        var ticket = await Api.StartTransaction(await Api.OpenSession());
        var line = (await Api.AddLine(ticket, product, 1)).GetProperty("lineId").GetString()!;
        await Api.Complete(ticket, "CARD", 11.9m);

        Assert.Equal(HttpStatusCode.BadRequest, (await AddLine(ticket, product, 1m, "EACH")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Api.Send(HttpMethod.Delete, $"/api/pos/transactions/{ticket}/lines/{line}")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Api.Post($"/api/pos/transactions/{ticket}/void", new { reason = "late" })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Api.Send(HttpMethod.Delete, $"/api/pos/transactions/{ticket}")).Status);
    }

    [Fact]
    public async Task A_ticket_that_already_has_lines_can_be_deleted_with_them()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 10, unitCost: 4m);
        var ticket = await Api.StartTransaction(await Api.OpenSession());
        await Api.AddLine(ticket, product, 1);
        await Api.AddLine(ticket, product, 2);

        Assert.Equal(HttpStatusCode.OK, (await Api.Send(HttpMethod.Delete, $"/api/pos/transactions/{ticket}")).Status);

        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM pos.transaction_line WHERE transaction_id = @t", ("t", ticket)));
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM pos.pos_transaction WHERE id = @t", ("t", ticket)));
    }

    [Fact]
    public async Task An_in_progress_ticket_can_be_deleted_but_a_closed_session_blocks_changes()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 10, unitCost: 4m);
        var session = await Api.OpenSession();
        var deleted = await Api.StartTransaction(session);
        var abandoned = await Api.StartTransaction(session);
        await Api.AddLine(abandoned, product, 1);

        Assert.Equal(HttpStatusCode.OK, (await Api.Send(HttpMethod.Delete, $"/api/pos/transactions/{deleted}")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Send(HttpMethod.Get, $"/api/pos/transactions/{deleted}")).Status);

        await Api.CloseSession(session, 50m); // closing does not touch tickets that were left open...
        Assert.Equal("IN_PROGRESS", (await Api.Transaction(abandoned)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Api.Complete(abandoned, "CARD", 11.9m)).Status); // ...but they cannot be paid
        Assert.Equal(HttpStatusCode.Conflict, (await AddLine(abandoned, product, 1m, "EACH")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Api.Post($"/api/pos/transactions/{abandoned}/void", new { reason = "x" })).Status);
        Assert.Equal(10m, await Api.Available(product));
    }

    // ---- phone scanner ---------------------------------------------------------------------

    [Fact]
    public async Task The_phone_scanner_adds_scanned_products_to_the_ticket_with_its_token()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 10, unitCost: 4m);
        var sku = (await Api.GetOk($"/api/inventory/products/{product}")).GetProperty("sku").GetString()!;
        var ticket = await Api.StartTransaction(await Api.OpenSession());
        var pairing = await Api.PostOk($"/api/pos/transactions/{ticket}/scanner-session");
        var session = pairing.GetProperty("sessionId").GetGuid();
        var token = pairing.GetProperty("token").GetString()!;

        using var phone = fixture.Factory.CreateClient(); // the phone has no login, only the pairing token
        Assert.Equal(HttpStatusCode.BadRequest, (await Scan(phone, session, "wrong-token", sku)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Scan(phone, session, null, sku)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Scan(phone, session, token, "NO-SUCH-SKU")).StatusCode);

        var scanned = await Scan(phone, session, token, "  " + sku.ToLowerInvariant() + " ");
        Assert.Equal(HttpStatusCode.OK, scanned.StatusCode);
        var result = await scanned.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(product, result.GetProperty("productId").GetGuid());
        Assert.Equal("1 unit(s)", result.GetProperty("quantity").GetString());
        Assert.Equal("11.900 TND", result.GetProperty("linePrice").GetString());
        Assert.Single((await Api.Transaction(ticket)).GetProperty("lines").EnumerateArray());

        using var close = new HttpRequestMessage(HttpMethod.Delete, $"/api/pos/scanner-sessions/{session}");
        close.Headers.Add("X-Scanner-Token", token);
        Assert.Equal(HttpStatusCode.NoContent, (await phone.SendAsync(close)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Scan(phone, session, token, sku)).StatusCode); // pairing is gone
    }

    [Fact]
    public async Task A_finished_ticket_cannot_be_paired_with_the_scanner()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 10, unitCost: 4m);
        var ticket = await Api.StartTransaction(await Api.OpenSession());
        await Api.AddLine(ticket, product, 1);
        await Api.Complete(ticket, "CARD", 11.9m);

        Assert.Equal(HttpStatusCode.Conflict, (await Api.Post($"/api/pos/transactions/{ticket}/scanner-session")).Status);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private Task<(HttpStatusCode Status, JsonElement Body)> AddLine(Guid ticket, Guid product, decimal quantity, string unit) =>
        Api.Post($"/api/pos/transactions/{ticket}/lines", new { productId = product, quantityValue = quantity, unitType = unit });

    private static Task<HttpResponseMessage> Scan(HttpClient phone, Guid session, string? token, string barcode)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/pos/scanner-sessions/{session}/scans")
        {
            Content = JsonContent.Create(new { barcode })
        };
        if (token is not null) request.Headers.Add("X-Scanner-Token", token);
        return phone.SendAsync(request);
    }
}
