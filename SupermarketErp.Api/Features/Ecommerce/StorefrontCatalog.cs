using System.Globalization;
using Microsoft.Data.SqlClient;
using SupermarketErp.Api.Features.Auth;

namespace SupermarketErp.Api.Features.Ecommerce;

public sealed record StorefrontProductView(Guid Id, string Sku, string Name, Guid? FamilyId,
    string? FamilyName, string? FamilyImageUrl, string? ImageUrl, string UnitType, string UnitPrice,
    decimal VatRate, decimal AvailableQuantity, bool InStock);

public sealed class StorefrontCatalog(SqlServerDataSource dataSource)
{
    private const string Select = """
        SELECT p.id,p.sku,p.name,p.family_id,f.name,f.image_url,p.image_url,p.unit_type,
               COALESCE(price.unit_price,0) * (1-ROUND(COALESCE(promo.discount_percent,0)/100,4)),
               COALESCE(p.vat_rate,19),COALESCE(stock.available,0)
        FROM inventory.product p
        LEFT JOIN inventory.product_family f ON f.id=p.family_id
        LEFT JOIN pricing.product_price price ON price.product_id=p.id
        LEFT JOIN LATERAL (
            SELECT discount_percent FROM pricing.promotion pr
            WHERE pr.product_id=p.id AND pr.active=1 AND pr.valid_from<=@now AND pr.valid_until>=@now
            ORDER BY pr.valid_from LIMIT 1
        ) promo ON 1
        LEFT JOIN LATERAL (
            SELECT SUM(le.on_hand_value-le.reserved_value) AS available
            FROM inventory.stock_level sl JOIN inventory.lot_entry le ON le.stock_level_id=sl.id
            WHERE sl.product_id=p.id
        ) stock ON 1
        WHERE p.online_visible=1 AND price.unit_price IS NOT NULL
          AND (@family IS NULL OR p.family_id=@family)
          AND (@search IS NULL OR p.name ILIKE @pattern OR p.sku ILIKE @pattern)
        """;

    public async Task<IReadOnlyList<StorefrontProductView>> List(string? search, Guid? familyId, CancellationToken ct)
    {
        if (search?.Length > 120) throw new ArgumentException("Search cannot exceed 120 characters.");
        var list = new List<StorefrontProductView>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new SqlCommand(Select + " ORDER BY p.name,p.sku", connection);
        AddFilters(command, search, familyId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(Read(reader));
        return list;
    }

    public async Task<StorefrontProductView?> Get(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new SqlCommand(Select + " AND p.id=@id", connection);
        AddFilters(command, null, null);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    private static void AddFilters(SqlCommand command, string? search, Guid? familyId)
    {
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        command.Parameters.Add(new SqlParameter("family", System.Data.SqlDbType.UniqueIdentifier) { Value = (object?)familyId ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("search", System.Data.SqlDbType.NVarChar) { Value = (object?)search ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("pattern", System.Data.SqlDbType.NVarChar)
        {
            Value = (object?)(search is null ? null : $"%{search.Trim()}%") ?? DBNull.Value
        });
    }

    private static StorefrontProductView Read(SqlDataReader r)
    {
        var price = decimal.Round(r.GetDecimal(8), 3, MidpointRounding.AwayFromZero);
        var available = r.GetDecimal(10);
        return new StorefrontProductView(r.GetGuid(0), r.GetString(1), r.GetString(2),
            r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7),
            $"{price.ToString("0.000", CultureInfo.InvariantCulture)} TND", r.GetDecimal(9), available, available > 0);
    }
}

public static class StorefrontCatalogEndpoints
{
    public static IEndpointRouteBuilder MapStorefrontCatalog(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/store/products").WithTags("Storefront catalog");
        group.MapGet("", async (string? search, Guid? familyId, StorefrontCatalog catalog, CancellationToken ct) =>
            Results.Ok(await catalog.List(search, familyId, ct))).AllowAnonymous();
        group.MapGet("/{productId:guid}", async (Guid productId, StorefrontCatalog catalog, CancellationToken ct) =>
            await catalog.Get(productId, ct) is { } product ? Results.Ok(product) : Results.NotFound()).AllowAnonymous();
        return endpoints;
    }
}

