using System.Globalization;
using Microsoft.Data.SqlClient;
using SupermarketErp.Api.Features.Auth;

namespace SupermarketErp.Api.Features.Pricing;

public sealed record SetPriceRequest(Guid ProductId, decimal UnitPrice);
public sealed record PriceView(Guid ProductId, string UnitPrice);
public sealed record CreatePromotionRequest(Guid ProductId, decimal DiscountPercent,
    DateTimeOffset ValidFrom, DateTimeOffset ValidUntil);
public sealed record PromotionView(Guid Id, Guid ProductId, decimal DiscountPercent,
    DateTimeOffset ValidFrom, DateTimeOffset ValidUntil, bool Active);

public static class PricingEndpoints
{
    public static IEndpointRouteBuilder MapPricingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var pricing = endpoints.MapGroup("/api/pricing").WithTags("Pricing");
        pricing.MapGet("/prices", async (PricingRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListPrices(ct)))
            .RequireAuthorization(Policies.StaffRead);
        pricing.MapGet("/prices/{productId:guid}", async (Guid productId, PricingRepository repository, CancellationToken ct) =>
            await repository.GetPrice(productId, ct) is { } price ? Results.Ok(price) : Results.NotFound())
            .RequireAuthorization(Policies.StaffRead);
        pricing.MapPost("/prices", async (SetPriceRequest request, PricingRepository repository, CancellationToken ct) =>
        {
            await repository.SetPrice(request, ct);
            return Results.Ok(new { productId = request.ProductId });
        }).RequireAuthorization(Policies.InventoryWrite);
        pricing.MapDelete("/prices/{productId:guid}", async (Guid productId, PricingRepository repository, CancellationToken ct) =>
            await repository.DeletePrice(productId, ct) ? Results.Ok() : Results.NotFound())
            .RequireAuthorization(Policies.InventoryWrite);

        pricing.MapGet("/promotions", async (PricingRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListPromotions(null, ct)))
            .RequireAuthorization(Policies.StaffRead);
        pricing.MapGet("/promotions/{productId:guid}", async (Guid productId, PricingRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListPromotions(productId, ct)))
            .RequireAuthorization(Policies.StaffRead);
        pricing.MapPost("/promotions", async (CreatePromotionRequest request, PricingRepository repository, CancellationToken ct) =>
        {
            var promotionId = await repository.CreatePromotion(request, ct);
            return Results.Ok(new { promotionId });
        }).RequireAuthorization(Policies.InventoryWrite);
        pricing.MapDelete("/promotions/{promotionId:guid}", async (Guid promotionId, PricingRepository repository, CancellationToken ct) =>
            await repository.DeactivatePromotion(promotionId, ct) ? Results.Ok() : Results.NotFound())
            .RequireAuthorization(Policies.InventoryWrite);
        return endpoints;
    }
}

public sealed class PricingRepository(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<PriceView>> ListPrices(CancellationToken ct)
    {
        var prices = new List<PriceView>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT product_id, unit_price FROM pricing.product_price ORDER BY product_id", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) prices.Add(ToPrice(reader.GetGuid(0), reader.GetDecimal(1)));
        return prices;
    }

    public async Task<PriceView?> GetPrice(Guid productId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT unit_price FROM pricing.product_price WHERE product_id = @product_id", connection);
        command.Parameters.AddWithValue("product_id", productId);
        var result = await command.ExecuteScalarAsync(ct);
        return result is decimal amount ? ToPrice(productId, amount) : null;
    }

    public async Task SetPrice(SetPriceRequest request, CancellationToken ct)
    {
        if (request.ProductId == Guid.Empty) throw new ArgumentException("Product ID is required.");
        if (request.UnitPrice <= 0) throw new ArgumentException("Unit price must be positive.");
        var amount = decimal.Round(request.UnitPrice, 3, MidpointRounding.AwayFromZero);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO pricing.product_price(id, product_id, unit_price)
            VALUES(@id, @product_id, @unit_price)
            ON CONFLICT(product_id) DO UPDATE SET unit_price = EXCLUDED.unit_price
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("product_id", request.ProductId);
        command.Parameters.AddWithValue("unit_price", amount);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeletePrice(Guid productId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "DELETE FROM pricing.product_price WHERE product_id = @product_id", connection);
        command.Parameters.AddWithValue("product_id", productId);
        return await command.ExecuteNonQueryAsync(ct) != 0;
    }

    public async Task<Guid> CreatePromotion(CreatePromotionRequest request, CancellationToken ct)
    {
        if (request.ProductId == Guid.Empty) throw new ArgumentException("Product ID is required.");
        if (request.DiscountPercent is < 0 or > 100) throw new ArgumentException("Discount must be between 0 and 100 percent.");
        if (request.ValidUntil <= request.ValidFrom) throw new ArgumentException("Promotion end must be after its start.");
        var id = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO pricing.promotion(id, product_id, type, discount_percent, valid_from, valid_until, active)
            VALUES(@id, @product_id, 'PERCENTAGE_DISCOUNT', @discount, @valid_from, @valid_until, 1)
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("product_id", request.ProductId);
        command.Parameters.AddWithValue("discount", request.DiscountPercent);
        command.Parameters.AddWithValue("valid_from", request.ValidFrom.ToUniversalTime());
        command.Parameters.AddWithValue("valid_until", request.ValidUntil.ToUniversalTime());
        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<IReadOnlyList<PromotionView>> ListPromotions(Guid? productId, CancellationToken ct)
    {
        const string sql = """
            SELECT id, product_id, discount_percent, valid_from, valid_until, active
            FROM pricing.promotion
            WHERE (@product_id IS NULL OR product_id = @product_id)
            ORDER BY valid_from DESC, id
            """;
        var list = new List<PromotionView>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("product_id", (object?)productId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new PromotionView(reader.GetGuid(0), reader.GetGuid(1), reader.GetDecimal(2),
                ReadInstant(reader, 3), ReadInstant(reader, 4), reader.GetBoolean(5)));
        return list;
    }

    public async Task<bool> DeactivatePromotion(Guid promotionId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "UPDATE pricing.promotion SET active = 0 WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", promotionId);
        return await command.ExecuteNonQueryAsync(ct) != 0;
    }

    private static PriceView ToPrice(Guid productId, decimal amount) =>
        new(productId, $"{amount.ToString("0.000", CultureInfo.InvariantCulture)} TND");

    private static DateTimeOffset ReadInstant(NpgsqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}
