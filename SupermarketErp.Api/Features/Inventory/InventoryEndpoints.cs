using Microsoft.Data.SqlClient;
using SupermarketErp.Api.Features.Auth;

namespace SupermarketErp.Api.Features.Inventory;

public sealed record ReceiveStockRequest(
    Guid ProductId,
    string? LotNumber,
    decimal QuantityValue,
    string UnitType,
    DateOnly? ExpiryDate,
    DateOnly? ReceivedDate,
    string? SupplierReference,
    decimal? UnitCost);

public sealed record ReceiveStockResponse(string Status);
public sealed record LotView(string Id, string LotNumber, string ProductId, decimal Quantity,
    decimal ReservedQuantity, decimal AvailableQuantity, string? ExpiryDate, string? ReceivedAt,
    decimal UnitCost, string? SupplierReference);
public sealed record StockLevelView(Guid ProductId, string ProductName, decimal TotalQuantity,
    decimal ReservedQuantity, decimal AvailableQuantity, IReadOnlyList<LotView> Lots);
public sealed record StockSummaryView(Guid ProductId, string Sku, string Name, Guid? FamilyId,
    string? FamilyName, string? FamilyImageUrl, string? ImageUrl, string UnitType, bool Perishable,
    int ReorderPoint, decimal TotalQuantity, decimal ReservedQuantity, decimal AvailableQuantity,
    int LotCount, string? EarliestExpiryDate, string Status);
public sealed record CreateFamilyRequest(string Name, string? ImageUrl);
public sealed record FamilyView(Guid Id, string Name, string? ImageUrl);

public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var stock = endpoints.MapGroup("/api/inventory/stock").WithTags("Inventory stock");
        stock.MapGet("", async (InventoryRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListStock(ct)))
            .RequireAuthorization(Policies.StaffRead);
        stock.MapGet("/{productId:guid}", async (Guid productId, InventoryRepository repository, CancellationToken ct) =>
            await repository.GetStock(productId, ct) is { } result ? Results.Ok(result) : Results.NotFound())
            .RequireAuthorization(Policies.StaffRead);

        endpoints.MapPost("/api/inventory/receive-stock", async (
            ReceiveStockRequest request, InventoryRepository repository, CancellationToken ct) =>
        {
            await repository.ReceiveStock(request, ct);
            return Results.Ok(new ReceiveStockResponse("received"));
        }).WithTags("Inventory stock").RequireAuthorization(Policies.InventoryWrite);

        var families = endpoints.MapGroup("/api/inventory/product-families").WithTags("Product families");
        families.MapGet("", async (InventoryRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListFamilies(ct)))
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.InventoryManager);
        families.MapPost("", async (CreateFamilyRequest request, InventoryRepository repository, CancellationToken ct) =>
        {
            try
            {
                var id = await repository.CreateFamily(request, ct);
                return Results.Ok(new { familyId = id });
            }
            catch (InvalidOperationException)
            {
                return Results.Conflict();
            }
        }).RequireAuthorization(Policies.InventoryWrite);

        return endpoints;
    }
}

public sealed class InventoryRepository(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<FamilyView>> ListFamilies(CancellationToken ct)
    {
        var families = new List<FamilyView>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT id, name, image_url FROM inventory.product_family ORDER BY name", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            families.Add(new FamilyView(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return families;
    }

    public async Task<Guid> CreateFamily(CreateFamilyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Family name is required.");
        if (request.Name.Trim().Length > 160) throw new ArgumentException("Family name cannot exceed 160 characters.");
        if (request.ImageUrl?.Length > 500) throw new ArgumentException("Image URL cannot exceed 500 characters.");
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var exists = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM inventory.product_family WHERE lower(name) = lower(@name))", connection);
        exists.Parameters.AddWithValue("name", request.Name.Trim());
        if ((bool)(await exists.ExecuteScalarAsync(ct) ?? false))
            throw new InvalidOperationException("A product family with this name already exists.");
        var id = Guid.NewGuid();
        await using var insert = new NpgsqlCommand(
            "INSERT INTO inventory.product_family(id, name, image_url) VALUES(@id, @name, @image_url)", connection);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("name", request.Name.Trim());
        insert.Parameters.AddWithValue("image_url", (object?)Normalize(request.ImageUrl) ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<IReadOnlyList<StockSummaryView>> ListStock(CancellationToken ct)
    {
        const string sql = """
            SELECT p.id, p.sku, p.name, p.family_id, f.name, f.image_url, p.image_url, p.unit_type,
                   p.perishable, COALESCE(p.reorder_point, 0),
                   COALESCE(SUM(le.on_hand_value), 0), COALESCE(SUM(le.reserved_value), 0),
                   COUNT(*) FILTER (WHERE le.on_hand_value <> 0),
                   MIN(l.expiry_date) FILTER (WHERE le.on_hand_value <> 0 AND l.expiry_date IS NOT NULL)
            FROM inventory.product p
            LEFT JOIN inventory.product_family f ON f.id = p.family_id
            LEFT JOIN inventory.stock_level sl ON sl.product_id = p.id
            LEFT JOIN inventory.lot_entry le ON le.stock_level_id = sl.id
            LEFT JOIN inventory.lot l ON l.id = le.lot_id
            GROUP BY p.id, p.sku, p.name, p.family_id, f.name, f.image_url, p.image_url,
                     p.unit_type, p.perishable, p.reorder_point
            ORDER BY p.name, p.sku
            """;
        var list = new List<StockSummaryView>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var onHand = reader.GetDecimal(10);
            var reserved = reader.GetDecimal(11);
            var available = onHand - reserved;
            var reorder = reader.GetInt32(9);
            var status = available <= 0 ? "OUT_OF_STOCK" : available <= reorder ? "LOW_STOCK" : "IN_STOCK";
            list.Add(new StockSummaryView(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7), reader.GetBoolean(8), reorder, onHand, reserved, available,
                checked((int)reader.GetInt64(12)), reader.IsDBNull(13) ? null : reader.GetFieldValue<DateOnly>(13).ToString("yyyy-MM-dd"), status));
        }
        return list;
    }

    public async Task<StockLevelView?> GetStock(Guid productId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var productCommand = new NpgsqlCommand(
            "SELECT name FROM inventory.product WHERE id = @id", connection);
        productCommand.Parameters.AddWithValue("id", productId);
        var name = await productCommand.ExecuteScalarAsync(ct) as string;
        if (name is null) return null;

        const string sql = """
            SELECT l.id, l.lot_number, l.product_id, le.on_hand_value, le.reserved_value,
                   l.expiry_date, l.received_date, l.unit_cost, l.supplier_reference
            FROM inventory.stock_level sl
            JOIN inventory.lot_entry le ON le.stock_level_id = sl.id
            JOIN inventory.lot l ON l.id = le.lot_id
            WHERE sl.product_id = @product_id AND le.on_hand_value <> 0
            ORDER BY l.expiry_date NULLS LAST, l.received_date, l.id
            """;
        var lots = new List<LotView>();
        decimal total = 0, reserved = 0;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("product_id", productId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var onHand = reader.GetDecimal(3);
            var reservedQty = reader.GetDecimal(4);
            total += onHand;
            reserved += reservedQty;
            lots.Add(new LotView(reader.GetGuid(0).ToString(), reader.GetString(1), reader.GetGuid(2).ToString(),
                onHand, reservedQty, onHand - reservedQty,
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateOnly>(5).ToString("yyyy-MM-dd"),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateOnly>(6).ToDateTime(TimeOnly.MinValue).ToString("yyyy-MM-dd'T'HH:mm"),
                reader.IsDBNull(7) ? 0 : reader.GetDecimal(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return new StockLevelView(productId, name, total, reserved, total - reserved, lots);
    }

    public async Task ReceiveStock(ReceiveStockRequest request, CancellationToken ct)
    {
        if (request.QuantityValue <= 0) throw new ArgumentException("Quantity must be positive.");
        if (request.UnitCost is < 0) throw new ArgumentException("Unit cost cannot be negative.");
        var unitType = request.UnitType?.Trim().ToUpperInvariant();
        if (unitType is not ("EACH" or "WEIGHT")) throw new ArgumentException("Unit type must be EACH or WEIGHT.");
        if (unitType == "EACH" && request.QuantityValue != decimal.Truncate(request.QuantityValue))
            throw new ArgumentException("EACH quantities must be whole numbers.");
        var quantityValue = unitType == "WEIGHT"
            ? decimal.Round(request.QuantityValue, 3, MidpointRounding.AwayFromZero)
            : request.QuantityValue;

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        bool perishable;
        string productUnit;
        await using (var product = new NpgsqlCommand(
            "SELECT unit_type, perishable FROM inventory.product WHERE id = @id", connection, transaction))
        {
            product.Parameters.AddWithValue("id", request.ProductId);
            await using var reader = await product.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new ArgumentException($"Product not found: {request.ProductId}");
            productUnit = reader.GetString(0);
            perishable = reader.GetBoolean(1);
        }
        if (!productUnit.Equals(unitType, StringComparison.Ordinal))
            throw new ArgumentException("Requested unit type does not match the product.");
        if (perishable && request.ExpiryDate is null)
            throw new ArgumentException("Perishable products require an expiry date.");

        var stockId = Guid.NewGuid();
        await using (var stock = new NpgsqlCommand("""
            INSERT INTO inventory.stock_level(id, product_id, version) VALUES(@id, @product_id, 0)
            ON CONFLICT(product_id) DO UPDATE SET version = inventory.stock_level.version + 1
            RETURNING id
            """, connection, transaction))
        {
            stock.Parameters.AddWithValue("id", stockId);
            stock.Parameters.AddWithValue("product_id", request.ProductId);
            stockId = (Guid)(await stock.ExecuteScalarAsync(ct))!;
        }

        var lotId = Guid.NewGuid();
        var lotNumber = string.IsNullOrWhiteSpace(request.LotNumber)
            ? $"LOT-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"
            : request.LotNumber.Trim();
        await using (var lot = new NpgsqlCommand("""
            INSERT INTO inventory.lot(id, lot_number, product_id, expiry_date, received_date, supplier_reference, unit_cost)
            VALUES(@id, @lot_number, @product_id, @expiry_date, @received_date, @supplier_reference, @unit_cost)
            """, connection, transaction))
        {
            lot.Parameters.AddWithValue("id", lotId);
            lot.Parameters.AddWithValue("lot_number", lotNumber);
            lot.Parameters.AddWithValue("product_id", request.ProductId);
            lot.Parameters.AddWithValue("expiry_date", (object?)request.ExpiryDate ?? DBNull.Value);
            lot.Parameters.AddWithValue("received_date", (object?)request.ReceivedDate ?? DateOnly.FromDateTime(DateTime.Today));
            lot.Parameters.AddWithValue("supplier_reference", (object?)Normalize(request.SupplierReference) ?? DBNull.Value);
            lot.Parameters.AddWithValue("unit_cost", request.UnitCost ?? 0m);
            await lot.ExecuteNonQueryAsync(ct);
        }

        await using (var entry = new NpgsqlCommand("""
            INSERT INTO inventory.lot_entry
                (id, stock_level_id, lot_id, on_hand_value, on_hand_unit_type, reserved_value, reserved_unit_type)
            VALUES(@id, @stock_id, @lot_id, @quantity, @unit_type, 0, @unit_type)
            """, connection, transaction))
        {
            entry.Parameters.AddWithValue("id", Guid.NewGuid());
            entry.Parameters.AddWithValue("stock_id", stockId);
            entry.Parameters.AddWithValue("lot_id", lotId);
            entry.Parameters.AddWithValue("quantity", quantityValue);
            entry.Parameters.AddWithValue("unit_type", unitType == "EACH" ? (short)0 : (short)1);
            await entry.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
