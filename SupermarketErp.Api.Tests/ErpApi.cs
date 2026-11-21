using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SupermarketErp.Api.Tests;

/// <summary>Thin, intention-revealing wrapper over the public HTTP API used by the tests.</summary>
public sealed class ErpApi(ApiFixture fixture)
{
    private HttpClient Client => fixture.Client;

    // ---- plumbing -------------------------------------------------------------------------

    public async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (response.StatusCode, json);
    }

    public Task<(HttpStatusCode Status, JsonElement Body)> Post(string url, object? body = null) => Send(HttpMethod.Post, url, body);

    public async Task<JsonElement> PostOk(string url, object? body = null)
    {
        var (status, json) = await Post(url, body);
        Assert.True(status == HttpStatusCode.OK, $"POST {url} returned {(int)status}: {json}");
        return json;
    }

    public async Task<JsonElement> GetOk(string url)
    {
        var (status, json) = await Send(HttpMethod.Get, url);
        Assert.True(status == HttpStatusCode.OK, $"GET {url} returned {(int)status}: {json}");
        return json;
    }

    public static decimal Money(JsonElement value) => Money(value.GetString()!);
    public static decimal Money(string value) => decimal.Parse(value.Split(' ')[0], CultureInfo.InvariantCulture);

    public static async Task<T> Eventually<T>(Func<Task<T>> probe, Func<T, bool> ready, string what, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var result = await probe();
            if (ready(result)) return result;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for {what}.");
            await Task.Delay(100);
        }
    }

    // ---- inventory & pricing ---------------------------------------------------------------

    public async Task<Guid> CreateProduct(string unit = "EACH", bool perishable = false, int reorderPoint = 0, decimal? vatRate = null)
    {
        var json = await PostOk("/api/inventory/products", new
        {
            sku = "T-" + Guid.NewGuid().ToString("N")[..12],
            name = "Test product",
            unitType = unit,
            perishable,
            reorderPoint,
            vatRate
        });
        return json.GetProperty("productId").GetGuid();
    }

    public Task SetPrice(Guid productId, decimal unitPrice) =>
        PostOk("/api/pricing/prices", new { productId, unitPrice });

    public Task ReceiveStock(Guid productId, decimal quantity, decimal unitCost, string? lotNumber = null,
        DateOnly? expiry = null, string unit = "EACH") =>
        PostOk("/api/inventory/receive-stock", new
        {
            productId, lotNumber, quantityValue = quantity, unitType = unit, expiryDate = expiry, unitCost
        });

    /// <summary>A sellable product: priced, with one lot in stock.</summary>
    public async Task<Guid> ProductWithStock(decimal price, decimal quantity, decimal unitCost, int reorderPoint = 0)
    {
        var product = await CreateProduct(reorderPoint: reorderPoint);
        await SetPrice(product, price);
        await ReceiveStock(product, quantity, unitCost);
        return product;
    }

    public Task<JsonElement> Stock(Guid productId) => GetOk($"/api/inventory/stock/{productId}");

    public async Task<decimal> Available(Guid productId) =>
        (await Stock(productId)).GetProperty("availableQuantity").GetDecimal();

    // ---- point of sale -----------------------------------------------------------------------

    public async Task<Guid> OpenSession(decimal openingFloat = 50m)
    {
        var till = await PostOk("/api/pos/tills", new { tillNumber = "T-" + Guid.NewGuid().ToString("N")[..10] });
        var session = await PostOk("/api/pos/sessions/open",
            new { tillId = till.GetProperty("id").GetGuid(), cashierName = "Test Cashier", openingFloat });
        return session.GetProperty("cashSessionId").GetGuid();
    }

    public async Task<Guid> StartTransaction(Guid cashSessionId) =>
        (await PostOk("/api/pos/transactions/start", new { cashSessionId })).GetProperty("transactionId").GetGuid();

    public Task<JsonElement> AddLine(Guid transactionId, Guid productId, decimal quantity, string unit = "EACH") =>
        PostOk($"/api/pos/transactions/{transactionId}/lines", new { productId, quantityValue = quantity, unitType = unit });

    public Task<(HttpStatusCode Status, JsonElement Body)> Complete(Guid transactionId, string method = "CASH", decimal? amountReceived = null) =>
        Post($"/api/pos/transactions/{transactionId}/complete", new { paymentMethod = method, amountReceived });

    public Task<JsonElement> Transaction(Guid id) => GetOk($"/api/pos/transactions/{id}");

    public async Task<decimal> ExpectedCash(Guid sessionId) =>
        Money((await GetOk($"/api/pos/sessions/{sessionId}")).GetProperty("expectedCash"));

    public Task<(HttpStatusCode Status, JsonElement Body)> CloseSession(Guid sessionId, decimal countedCash) =>
        Post($"/api/pos/sessions/{sessionId}/close", new { countedCash });

    // ---- procurement -------------------------------------------------------------------------

    public async Task<Guid> CreateSupplier() =>
        (await PostOk("/api/procurement/suppliers", new { name = "Supplier " + Guid.NewGuid().ToString("N")[..8] }))
        .GetProperty("id").GetGuid();

    public async Task<Guid> CreateOrder(Guid supplierId, params (Guid Product, decimal Quantity, decimal UnitCost)[] lines) =>
        (await PostOk("/api/procurement/orders", new
        {
            supplierId,
            lines = lines.Select(l => new { productId = l.Product, quantityValue = l.Quantity, unitType = "EACH", unitCost = l.UnitCost })
        })).GetProperty("id").GetGuid();

    public Task PlaceOrder(Guid orderId) => PostOk($"/api/procurement/orders/{orderId}/place");

    public Task<JsonElement> Order(Guid orderId) => GetOk($"/api/procurement/orders/{orderId}");

    /// <summary>Order line ids, in the same order the lines were passed to <see cref="CreateOrder"/> is not guaranteed; use product mapping.</summary>
    public async Task<Dictionary<Guid, Guid>> OrderLineIdsByProduct(Guid orderId) =>
        (await Order(orderId)).GetProperty("lines").EnumerateArray()
            .ToDictionary(l => l.GetProperty("productId").GetGuid(), l => l.GetProperty("id").GetGuid());

    public static object ReceiptLine(Guid lineId, decimal quantity, DateOnly? expiry = null, string? lotNumber = null) =>
        new { purchaseOrderLineId = lineId, quantityValue = quantity, unitType = "EACH", lotNumber, expiryDate = expiry };

    public Task<(HttpStatusCode Status, JsonElement Body)> Receive(Guid orderId, params object[] lines) =>
        Post($"/api/procurement/orders/{orderId}/receive", new { lines });

    public async Task<string> OrderStatus(Guid orderId) => (await Order(orderId)).GetProperty("status").GetString()!;

    public async Task<JsonElement[]> Alerts(Guid productId) =>
        (await GetOk("/api/procurement/alerts")).EnumerateArray()
            .Where(a => a.GetProperty("productId").GetGuid() == productId).ToArray();

    // ---- finance -----------------------------------------------------------------------------

    public async Task<JsonElement?> FindJournal(string eventKey)
    {
        var journals = await GetOk("/api/finance/journals");
        foreach (var journal in journals.EnumerateArray())
            if (journal.GetProperty("eventKey").GetString() == eventKey) return journal;
        return null;
    }

    public async Task<JsonElement> WaitForJournal(string eventKey, int timeoutMs = 20_000) =>
        (await Eventually(() => FindJournal(eventKey), j => j.HasValue, $"journal '{eventKey}'", timeoutMs))!.Value;

    /// <summary>Sums debit and credit per account for a journal entry.</summary>
    public static Dictionary<string, (decimal Debit, decimal Credit)> Accounts(JsonElement journal) =>
        journal.GetProperty("lines").EnumerateArray()
            .GroupBy(l => l.GetProperty("account").GetString()!)
            .ToDictionary(g => g.Key,
                g => (g.Sum(l => Money(l.GetProperty("debit"))), g.Sum(l => Money(l.GetProperty("credit")))));

    public static void AssertBalanced(JsonElement journal)
    {
        var lines = journal.GetProperty("lines").EnumerateArray().ToArray();
        Assert.NotEmpty(lines);
        Assert.Equal(lines.Sum(l => Money(l.GetProperty("debit"))), lines.Sum(l => Money(l.GetProperty("credit"))));
    }

    /// <summary>Waits until the outbox has no unprocessed event mentioning <paramref name="referenceId"/>.</summary>
    public Task WaitForOutboxDrained(Guid referenceId) => Eventually(
        () => fixture.ScalarAsync<int>(
            "SELECT COUNT(*) FROM platform.dotnet_outbox WHERE processed_at IS NULL AND payload LIKE @p",
            ("p", $"%{referenceId}%")),
        pending => pending == 0,
        $"outbox events for {referenceId} to be processed");
}
