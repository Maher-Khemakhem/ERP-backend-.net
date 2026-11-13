using Microsoft.Data.SqlClient;

namespace SupermarketErp.Api.Features.Inventory;

public sealed class ProductRepository(SqlServerDataSource dataSource)
{
    private const string SelectColumns = """
        SELECT p.id, p.sku, p.name, p.family_id, f.name, f.image_url, p.image_url,
               p.unit_type, p.perishable, p.reorder_point, p.vat_rate
        FROM inventory.product p
        LEFT JOIN inventory.product_family f ON f.id = p.family_id
        """;

    public async Task<IReadOnlyList<ProductView>> List(CancellationToken ct)
    {
        var products = new List<ProductView>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new SqlCommand(SelectColumns + " ORDER BY p.name, p.sku", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) products.Add(ReadProduct(reader));
        return products;
    }

    public async Task<ProductView?> Get(Guid productId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new SqlCommand(SelectColumns + " WHERE p.id = @id", connection);
        command.Parameters.AddWithValue("id", productId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadProduct(reader) : null;
    }

    public async Task<Guid> Create(CreateProductRequest request, CancellationToken ct)
    {
        Validate(request);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        if (request.FamilyId is not null)
        {
            await using var familyCheck = new SqlCommand(
                "SELECT EXISTS(SELECT 1 FROM inventory.product_family WHERE id = @id)", connection);
            familyCheck.Parameters.AddWithValue("id", request.FamilyId.Value);
            if (!(bool)(await familyCheck.ExecuteScalarAsync(ct) ?? false))
                throw new ArgumentException($"Product family not found: {request.FamilyId}");
        }

        var id = Guid.NewGuid();
        await using var command = new SqlCommand("""
            INSERT INTO inventory.product
                (id, sku, name, family_id, image_url, unit_type, perishable, reorder_point, vat_rate, online_visible)
            VALUES
                (@id, @sku, @name, @family_id, @image_url, @unit_type, @perishable, @reorder_point, @vat_rate, 1)
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("sku", request.Sku.Trim());
        command.Parameters.AddWithValue("name", request.Name.Trim());
        command.Parameters.AddWithValue("family_id", (object?)request.FamilyId ?? DBNull.Value);
        command.Parameters.AddWithValue("image_url", (object?)NormalizeOptional(request.ImageUrl) ?? DBNull.Value);
        command.Parameters.AddWithValue("unit_type", request.UnitType.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("perishable", request.Perishable);
        command.Parameters.AddWithValue("reorder_point", request.ReorderPoint);
        command.Parameters.AddWithValue("vat_rate", request.VatRate ?? 19m);
        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<bool> Delete(Guid productId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new SqlCommand("DELETE FROM inventory.product WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", productId);
        return await command.ExecuteNonQueryAsync(ct) != 0;
    }

    private static ProductView ReadProduct(SqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetGuid(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetString(7),
        reader.GetBoolean(8),
        reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
        reader.IsDBNull(10) ? 19m : reader.GetDecimal(10));

    private static void Validate(CreateProductRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Sku)) throw new ArgumentException("SKU is required.");
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Name is required.");
        if (request.ImageUrl?.Length > 500) throw new ArgumentException("Image URL cannot exceed 500 characters.");
        if (request.UnitType is not null && request.UnitType.Trim().ToUpperInvariant() is not ("EACH" or "WEIGHT"))
            throw new ArgumentException("Unit type must be EACH or WEIGHT.");
        if (request.ReorderPoint < 0) throw new ArgumentException("Reorder point cannot be negative.");
        if (request.VatRate is < 0 or > 100) throw new ArgumentException("VAT rate must be between 0 and 100.");
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

