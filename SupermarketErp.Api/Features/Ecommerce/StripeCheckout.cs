using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Features.Auth;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Ecommerce;

public sealed class RetryableWebhookException(string message) : Exception(message) { }

public sealed record StripeConfigurationView(bool Enabled, string Currency, decimal Rate,
    long MinimumAmountCents, long MaximumAmountCents);
public sealed record StripeQuote(string Currency, decimal TndAmount, long AmountCents, decimal Rate);
public sealed record StripeCheckoutResult(string Url, StripeQuote Quote);

public static class StripeCheckoutEndpoints
{
    public static IEndpointRouteBuilder MapStripeCheckout(this IEndpointRouteBuilder endpoints)
    {
        var stripe = endpoints.MapGroup("/api/store/stripe").WithTags("Stripe test checkout");
        stripe.MapGet("/config", (StripeCheckoutService service) => Results.Ok(service.Configuration()))
            .RequireAuthorization(Roles.Customer);
        stripe.MapGet("/orders/{orderId:guid}/quote", async (Guid orderId, ClaimsPrincipal user,
            StripeCheckoutService service, CancellationToken ct) =>
            Results.Ok(await service.Quote(CustomerId(user), orderId, ct)))
            .RequireAuthorization(Roles.Customer);
        stripe.MapPost("/orders/{orderId:guid}/checkout", async (Guid orderId, ClaimsPrincipal user,
            StripeCheckoutService service, CancellationToken ct) =>
            Results.Ok(await service.Create(CustomerId(user), orderId, ct)))
            .RequireAuthorization(Roles.Customer);
        stripe.MapGet("/orders/{orderId:guid}/status", async (Guid orderId, ClaimsPrincipal user,
            StripeCheckoutService service, CancellationToken ct) =>
            Results.Ok(new { status = await service.Reconcile(CustomerId(user), orderId, ct) }))
            .RequireAuthorization(Roles.Customer);
        stripe.MapPost("/webhook", async (HttpRequest request, StripeCheckoutService service, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(ct);
            await service.Webhook(body, request.Headers["Stripe-Signature"].FirstOrDefault(), ct);
            return Results.Ok();
        }).AllowAnonymous();
        return endpoints;
    }

    private static Guid CustomerId(ClaimsPrincipal user) => Guid.TryParse(user.FindFirstValue("userId"), out var id)
        ? id : throw new InvalidOperationException("The authenticated token has no user ID.");
}

public sealed class StripeCheckoutService(
    BusinessDbContext db,
    IHttpClientFactory clients,
    IConfiguration configuration,
    StorefrontOrderService orders)
{
    private readonly string _secretKey = configuration["STRIPE_SECRET_KEY"] ?? "";
    private readonly string _webhookSecret = configuration["STRIPE_WEBHOOK_SECRET"] ?? "";
    private readonly string _returnBaseUrl = configuration["STRIPE_STOREFRONT_RETURN_BASE_URL"] ?? "";
    private readonly decimal _rate = decimal.TryParse(configuration["STRIPE_TND_TO_EUR_RATE"], CultureInfo.InvariantCulture, out var rate) ? rate : 0;
    private const long MinimumCents = 50;
    private const long MaximumCents = 99_999_999;

    public StripeConfigurationView Configuration() => new(IsConfigured, "EUR", _rate, MinimumCents, MaximumCents);

    public async Task<StripeQuote> Quote(Guid customerId, Guid orderId, CancellationToken ct)
    {
        RequireConfigured();
        var order = await ReadOwnedOrder(customerId, orderId, ct);
        if (order.Status != "PENDING_PAYMENT" || order.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Only an unexpired pending order can start Stripe Checkout.");
        var existing = await GetCheckoutByOrder(orderId, ct);
        if (existing is not null) return ToQuote(existing);
        return ConvertAmount(order.Total);
    }

    public async Task<StripeCheckoutResult> Create(Guid customerId, Guid orderId, CancellationToken ct)
    {
        var quote = await Quote(customerId, orderId, ct);
        var order = await ReadOwnedOrder(customerId, orderId, ct);
        var checkout = await PrepareCheckout(order, quote, ct);
        if (checkout.Status == "EXPIRED") throw new InvalidOperationException("Stripe Checkout expired; cancel this order and place a new one.");
        if (!string.IsNullOrWhiteSpace(checkout.Url)) return new StripeCheckoutResult(checkout.Url, ToQuote(checkout));

        var baseUrl = _returnBaseUrl.EndsWith('/') ? _returnBaseUrl : _returnBaseUrl + "/";
        var success = $"{baseUrl}?stripe_return=success&order={orderId}";
        var cancel = $"{baseUrl}?stripe_return=cancel&order={orderId}";
        var form = new Dictionary<string, string>
        {
            ["mode"] = "payment",
            ["payment_method_types[0]"] = "card",
            ["line_items[0][price_data][currency]"] = "eur",
            ["line_items[0][price_data][unit_amount]"] = checkout.AmountCents.ToString(CultureInfo.InvariantCulture),
            ["line_items[0][price_data][product_data][name]"] = $"Online order {orderId}",
            ["line_items[0][quantity]"] = "1",
            ["client_reference_id"] = checkout.Id.ToString(),
            ["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(31).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["success_url"] = success,
            ["cancel_url"] = cancel
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.stripe.com/v1/checkout/sessions")
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secretKey);
        request.Headers.Add("Idempotency-Key", checkout.Id.ToString());
        using var response = await clients.CreateClient("stripe").SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Stripe Checkout request failed (HTTP {(int)response.StatusCode}).");
        using var document = JsonDocument.Parse(json);
        var session = ReadSession(document.RootElement);
        if (!session.Id.StartsWith("cs_test_", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(session.Url)
            || !session.Url.StartsWith("https://checkout.stripe.com/", StringComparison.Ordinal))
            throw new InvalidOperationException("Stripe returned an invalid test Checkout session.");

        var saved = await db.StripeCheckouts.FirstOrDefaultAsync(x => x.Id == checkout.Id && x.Status == "CREATING", ct);
        if (saved is not null)
        {
            saved.SessionId = session.Id;
            saved.Url = session.Url;
            saved.Status = "PENDING";
            saved.Version++;
            await db.SaveChangesAsync(ct);
        }
        return new StripeCheckoutResult(session.Url, ToQuote(checkout));
    }

    public async Task<string> Reconcile(Guid customerId, Guid orderId, CancellationToken ct)
    {
        _ = await ReadOwnedOrder(customerId, orderId, ct);
        var checkout = await GetCheckoutByOrder(orderId, ct) ?? throw new ArgumentException("Stripe Checkout not found.");
        if (checkout.Status == "PAID" || checkout.SessionId is null) return checkout.Status;
        var session = await Retrieve(checkout.SessionId, ct);
        if (session.Status == "expired")
        {
            await UpdateCheckoutStatus(checkout.Id, "EXPIRED", ct);
            return "EXPIRED";
        }
        if (session.PaymentStatus == "paid") await CompleteIfPaid(checkout, session, ct);
        return (await GetCheckoutByOrder(orderId, ct))?.Status ?? checkout.Status;
    }

    public async Task Webhook(string payload, string? signature, CancellationToken ct)
    {
        RequireConfigured();
        VerifyWebhook(payload, signature);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "checkout.session.completed") return;
        var session = ReadSession(root.GetProperty("data").GetProperty("object"));
        var checkout = await GetCheckoutBySession(session.Id, ct);
        if (checkout is null)
        {
            if (Guid.TryParse(session.ClientReferenceId, out var attemptId)
                && await CheckoutExists(attemptId, ct))
                throw new RetryableWebhookException("Stripe checkout is still being saved; retry the webhook.");
            return;
        }
        await CompleteIfPaid(checkout, session, ct);
    }

    private async Task CompleteIfPaid(CheckoutRow checkout, StripeSession session, CancellationToken ct)
    {
        if (checkout.Status == "PAID" || session.PaymentStatus != "paid") return;
        if (checkout.SessionId != session.Id || checkout.Id.ToString() != session.ClientReferenceId
            || session.Currency != "eur" || session.AmountTotal != checkout.AmountCents || session.LiveMode)
            throw new InvalidOperationException("Stripe Checkout does not match the saved test-mode order quote.");
        var order = await ReadOrderById(checkout.OrderId, ct);
        if (order.Total != checkout.TndAmount)
            throw new InvalidOperationException("Online order total changed after payment; manual refund required.");
        await orders.ConfirmStripePayment(checkout.OrderId, "STRIPE-" + checkout.SessionId, ct);
        await UpdateCheckoutStatus(checkout.Id, "PAID", ct);
    }

    private async Task<CheckoutRow> PrepareCheckout(OwnedOrder order, StripeQuote quote, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var existingEntity = await db.StripeCheckouts.FirstOrDefaultAsync(x => x.OrderId == order.Id, ct);
        var existing = existingEntity is null ? null : ToRow(existingEntity);
        if (existing?.Status == "EXPIRED") { await tx.CommitAsync(ct); return existing; }
        if (existing is not null) { await tx.CommitAsync(ct); return existing; }
        var row = new CheckoutRow(Guid.NewGuid(), order.Id, null, null, order.Total, quote.AmountCents, quote.Rate, "CREATING");
        db.StripeCheckouts.Add(new StripeCheckoutEntity { Id = row.Id, OrderId = row.OrderId, TndAmount = row.TndAmount, AmountCents = row.AmountCents, Rate = row.Rate, Status = row.Status, CreatedAt = DateTimeOffset.UtcNow });
        var orderEntity = await db.OnlineOrders.FirstOrDefaultAsync(x => x.Id == order.Id && x.Status == "PENDING_PAYMENT", ct)
            ?? throw new InvalidOperationException("Order is no longer pending payment.");
        var deadline = DateTimeOffset.UtcNow.AddMinutes(35);
        if (orderEntity.ExpiresAt < deadline) orderEntity.ExpiresAt = deadline;
        orderEntity.Version++;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return row;
    }

    private async Task<CheckoutRow?> GetCheckoutByOrder(Guid orderId, CancellationToken ct)
    {
        var row = await db.StripeCheckouts.AsNoTracking().FirstOrDefaultAsync(x => x.OrderId == orderId, ct);
        return row is null ? null : ToRow(row);
    }

    private async Task<CheckoutRow?> GetCheckoutBySession(string session, CancellationToken ct)
    {
        var row = await db.StripeCheckouts.AsNoTracking().FirstOrDefaultAsync(x => x.SessionId == session, ct);
        return row is null ? null : ToRow(row);
    }

    private async Task<bool> CheckoutExists(Guid id, CancellationToken ct)
    {
        return await db.StripeCheckouts.AnyAsync(x => x.Id == id, ct);
    }

    private async Task<OwnedOrder> ReadOwnedOrder(Guid customerId, Guid id, CancellationToken ct)
    {
        var row = await db.OnlineOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == customerId, ct)
            ?? throw new ArgumentException("Order not found.");
        return new OwnedOrder(row.Id, row.CustomerId, row.Status, row.TotalAmount, row.ExpiresAt);
    }

    private async Task<OwnedOrder> ReadOrderById(Guid id, CancellationToken ct)
    {
        var row = await db.OnlineOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ArgumentException("Order not found.");
        return new OwnedOrder(row.Id, row.CustomerId, row.Status, row.TotalAmount, row.ExpiresAt);
    }

    private async Task<StripeSession> Retrieve(string sessionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.stripe.com/v1/checkout/sessions/{Uri.EscapeDataString(sessionId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secretKey);
        using var response = await clients.CreateClient("stripe").SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Stripe status request failed (HTTP {(int)response.StatusCode}).");
        using var document = JsonDocument.Parse(body);
        return ReadSession(document.RootElement);
    }

    private async Task UpdateCheckoutStatus(Guid id, string status, CancellationToken ct)
    {
        var row = await db.StripeCheckouts.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is not null) { row.Status = status; row.Version++; await db.SaveChangesAsync(ct); }
    }

    private void VerifyWebhook(string payload, string? signature)
    {
        if (!_webhookSecret.StartsWith("whsec_", StringComparison.Ordinal)) throw new InvalidOperationException("Storefront Stripe test mode is not configured.");
        if (string.IsNullOrWhiteSpace(signature)) throw new ArgumentException("Missing Stripe signature.");
        string? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in signature.Split(','))
        {
            if (part.StartsWith("t=", StringComparison.Ordinal)) timestamp = part[2..];
            if (part.StartsWith("v1=", StringComparison.Ordinal)) signatures.Add(part[3..]);
        }
        if (!long.TryParse(timestamp, out var seconds) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seconds) > 300)
            throw new ArgumentException("Invalid Stripe signature timestamp.");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_webhookSecret));
        var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        foreach (var value in signatures)
        {
            try
            {
                if (CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(value))) return;
            }
            catch (FormatException) { }
        }
        throw new ArgumentException("Invalid Stripe signature.");
    }

    private StripeQuote ConvertAmount(decimal amount)
    {
        if (_rate <= 0) throw new InvalidOperationException("Storefront Stripe test mode is not configured.");
        var cents = decimal.ToInt64(decimal.Round(amount * _rate * 100m, 0, MidpointRounding.AwayFromZero));
        if (cents < MinimumCents || cents > MaximumCents) throw new ArgumentException("Converted EUR charge must be between 0.50 and 999,999.99.");
        return new StripeQuote("EUR", amount, cents, _rate);
    }

    private static StripeQuote ToQuote(CheckoutRow row) => new("EUR", row.TndAmount, row.AmountCents, row.Rate);
    private static CheckoutRow ToRow(StripeCheckoutEntity x) => new(x.Id, x.OrderId, x.SessionId, x.Url, x.TndAmount, x.AmountCents, x.Rate, x.Status);
    private static StripeSession ReadSession(JsonElement json) => new(
        GetString(json, "id"), GetString(json, "client_reference_id"), GetString(json, "currency"),
        json.TryGetProperty("amount_total", out var amount) && amount.ValueKind == JsonValueKind.Number ? amount.GetInt64() : -1,
        GetString(json, "payment_status"), json.TryGetProperty("livemode", out var live) && live.ValueKind == JsonValueKind.True,
        GetString(json, "status"), GetString(json, "url"));
    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private void RequireConfigured()
    {
        if (!IsConfigured) throw new InvalidOperationException("Storefront Stripe test mode is not configured.");
    }
    private bool IsConfigured => _secretKey.StartsWith("sk_test_", StringComparison.Ordinal)
        && _webhookSecret.StartsWith("whsec_", StringComparison.Ordinal)
        && _rate > 0 && _returnBaseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    private sealed record OwnedOrder(Guid Id, Guid CustomerId, string Status, decimal Total, DateTimeOffset ExpiresAt);
    private sealed record CheckoutRow(Guid Id, Guid OrderId, string? SessionId, string? Url, decimal TndAmount,
        long AmountCents, decimal Rate, string Status);
    private sealed record StripeSession(string Id, string ClientReferenceId, string Currency, long AmountTotal,
        string PaymentStatus, bool LiveMode, string Status, string Url);
}

