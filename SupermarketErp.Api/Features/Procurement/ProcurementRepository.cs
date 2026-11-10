using Microsoft.Data.SqlClient;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Procurement;
public sealed class ProcurementRepository(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<SupplierView>> ListSuppliers(CancellationToken ct)
    {
        var list = new List<SupplierView>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT id,name,contact_email,contact_phone,address FROM procurement.supplier ORDER BY name", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(ReadSupplier(reader));
        return list;
    }

    public async Task<SupplierView?> GetSupplier(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT id,name,contact_email,contact_phone,address FROM procurement.supplier WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSupplier(reader) : null;
    }

    public async Task<Guid> CreateSupplier(SupplierRequest request, CancellationToken ct)
    {
        ValidateSupplier(request);
        var id = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO procurement.supplier(id,name,contact_email,contact_phone,address)
            VALUES(@id,@name,@email,@phone,@address)
            """, connection);
        AddSupplierParameters(command, id, request);
        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<bool> UpdateSupplier(Guid id, SupplierRequest request, CancellationToken ct)
    {
        ValidateSupplier(request);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            UPDATE procurement.supplier SET name=@name,contact_email=@email,contact_phone=@phone,address=@address WHERE id=@id
            """, connection);
        AddSupplierParameters(command, id, request);
        return await command.ExecuteNonQueryAsync(ct) != 0;
    }

    public async Task<bool> DeleteSupplier(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("DELETE FROM procurement.supplier WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync(ct) != 0;
    }

    public async Task<Guid> CreateOrder(CreateOrderRequest request, CancellationToken ct)
    {
        if (request.SupplierId == Guid.Empty) throw new ArgumentException("Supplier ID is required.");
        if (request.Lines is null || request.Lines.Count == 0) throw new ArgumentException("At least one order line is required.");
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        await using (var supplier = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM procurement.supplier WHERE id=@id)", connection, transaction))
        {
            supplier.Parameters.AddWithValue("id", request.SupplierId);
            if (!(bool)(await supplier.ExecuteScalarAsync(ct) ?? false)) throw new ArgumentException("Supplier not found.");
        }

        var orderId = Guid.NewGuid();
        await using (var order = new NpgsqlCommand("""
            INSERT INTO procurement.purchase_order(id,supplier_id,status,placed_at) VALUES(@id,@supplier,'DRAFT',@now)
            """, connection, transaction))
        {
            order.Parameters.AddWithValue("id", orderId);
            order.Parameters.AddWithValue("supplier", request.SupplierId);
            order.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
            await order.ExecuteNonQueryAsync(ct);
        }
        foreach (var line in request.Lines)
        {
            ValidateQuantity(line.QuantityValue, line.UnitType);
            if (line.UnitCost < 0) throw new ArgumentException("Unit cost cannot be negative.");
            await using var product = new NpgsqlCommand("SELECT unit_type FROM inventory.product WHERE id=@id", connection, transaction);
            product.Parameters.AddWithValue("id", line.ProductId);
            var productUnit = await product.ExecuteScalarAsync(ct) as string;
            if (productUnit is null || !productUnit.Equals(line.UnitType, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Product or unit type is invalid: {line.ProductId}");
            await using var insertLine = new NpgsqlCommand("""
                INSERT INTO procurement.purchase_order_line(id,purchase_order_id,product_id,value,unit_type,unit_cost)
                VALUES(@id,@order,@product,@quantity,@unit,@cost)
                """, connection, transaction);
            insertLine.Parameters.AddWithValue("id", Guid.NewGuid());
            insertLine.Parameters.AddWithValue("order", orderId);
            insertLine.Parameters.AddWithValue("product", line.ProductId);
            insertLine.Parameters.AddWithValue("quantity", line.QuantityValue);
            insertLine.Parameters.AddWithValue("unit", line.UnitType.Equals("EACH", StringComparison.OrdinalIgnoreCase) ? (short)0 : (short)1);
            insertLine.Parameters.AddWithValue("cost", line.UnitCost);
            await insertLine.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return orderId;
    }

    public async Task PlaceOrder(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        string status;
        await using (var order = new NpgsqlCommand("SELECT status FROM procurement.purchase_order WHERE id=@id FOR UPDATE", connection, transaction))
        {
            order.Parameters.AddWithValue("id", id);
            status = await order.ExecuteScalarAsync(ct) as string
                ?? throw new ArgumentException($"Purchase order not found: {id}");
        }
        if (status != "DRAFT") throw new InvalidOperationException($"Purchase order {id}: expected DRAFT, current status {status}");
        await using (var hasLines = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM procurement.purchase_order_line WHERE purchase_order_id=@id)", connection, transaction))
        {
            hasLines.Parameters.AddWithValue("id", id);
            if (!(bool)(await hasLines.ExecuteScalarAsync(ct) ?? false))
                throw new InvalidOperationException("Impossible d'envoyer une commande sans lignes");
        }
        await using var update = new NpgsqlCommand("UPDATE procurement.purchase_order SET status='PLACED' WHERE id=@id", connection, transaction);
        update.Parameters.AddWithValue("id", id);
        await update.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task CancelOrder(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        string status;
        await using (var order = new NpgsqlCommand("SELECT status FROM procurement.purchase_order WHERE id=@id FOR UPDATE", connection, transaction))
        {
            order.Parameters.AddWithValue("id", id);
            status = await order.ExecuteScalarAsync(ct) as string
                ?? throw new ArgumentException($"Purchase order not found: {id}");
        }
        if (status is not ("DRAFT" or "PLACED"))
            throw new InvalidOperationException($"Commande {id} : seule une commande DRAFT ou PLACED peut être annulée");
        await using var update = new NpgsqlCommand("UPDATE procurement.purchase_order SET status='CANCELLED' WHERE id=@id", connection, transaction);
        update.Parameters.AddWithValue("id", id);
        await update.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task ReceiveOrder(Guid id, ReceiveOrderRequest request, CancellationToken ct)
    {
        if (request.Lines is null || request.Lines.Count == 0)
            throw new ArgumentException("A receipt must contain every order line.");
        if (request.Lines.Select(x => x.PurchaseOrderLineId).Distinct().Count() != request.Lines.Count)
            throw new ArgumentException("A receipt cannot contain duplicate order lines.");

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        string status;
        Guid supplierId;
        await using (var order = new NpgsqlCommand("SELECT status,supplier_id FROM procurement.purchase_order WHERE id=@id FOR UPDATE", connection, transaction))
        {
            order.Parameters.AddWithValue("id", id);
            await using var reader = await order.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new ArgumentException($"Purchase order not found: {id}");
            status = reader.GetString(0);
            supplierId = reader.GetGuid(1);
        }
        if (status != "PLACED") throw new InvalidOperationException("Only a placed purchase order can be received.");

        var orderLines = new Dictionary<Guid, (Guid ProductId, decimal Quantity, short UnitType, decimal UnitCost)>();
        await using (var lines = new NpgsqlCommand("""
            SELECT id,product_id,value,unit_type,unit_cost FROM procurement.purchase_order_line
            WHERE purchase_order_id=@id
            """, connection, transaction))
        {
            lines.Parameters.AddWithValue("id", id);
            await using var reader = await lines.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                orderLines.Add(reader.GetGuid(0), (reader.GetGuid(1), reader.GetDecimal(2), reader.GetInt16(3), reader.GetDecimal(4)));
        }
        if (orderLines.Count != request.Lines.Count || request.Lines.Any(line => !orderLines.ContainsKey(line.PurchaseOrderLineId)))
            throw new ArgumentException("A receipt must include every order line exactly once.");

        foreach (var receipt in request.Lines)
        {
            ValidateQuantity(receipt.QuantityValue, receipt.UnitType);
            var ordered = orderLines[receipt.PurchaseOrderLineId];
            var receiptUnit = receipt.UnitType.Equals("EACH", StringComparison.OrdinalIgnoreCase) ? (short)0 : (short)1;
            if (receiptUnit != ordered.UnitType || receipt.QuantityValue != ordered.Quantity)
                throw new ArgumentException("Received quantity and unit must match the purchase order.");

            bool perishable;
            await using (var product = new NpgsqlCommand("SELECT perishable FROM inventory.product WHERE id=@id", connection, transaction))
            {
                product.Parameters.AddWithValue("id", ordered.ProductId);
                var value = await product.ExecuteScalarAsync(ct);
                if (value is null) throw new ArgumentException($"Product not found: {ordered.ProductId}");
                perishable = (bool)value;
            }
            if (perishable && receipt.ExpiryDate is null)
                throw new ArgumentException("Perishable products require an expiry date.");

            var stockLevelId = Guid.NewGuid();
            await using (var stock = new NpgsqlCommand("""
                INSERT INTO inventory.stock_level(id,product_id,version) VALUES(@id,@product,0)
                ON CONFLICT(product_id) DO UPDATE SET version=inventory.stock_level.version+1 RETURNING id
                """, connection, transaction))
            {
                stock.Parameters.AddWithValue("id", stockLevelId);
                stock.Parameters.AddWithValue("product", ordered.ProductId);
                stockLevelId = (Guid)(await stock.ExecuteScalarAsync(ct))!;
            }
            var lotId = Guid.NewGuid();
            var lotNumber = string.IsNullOrWhiteSpace(receipt.LotNumber)
                ? $"LOT-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"
                : receipt.LotNumber.Trim();
            await using (var lot = new NpgsqlCommand("""
                INSERT INTO inventory.lot(id,lot_number,product_id,expiry_date,received_date,supplier_reference,unit_cost)
                VALUES(@id,@number,@product,@expiry,@received,@reference,@cost)
                """, connection, transaction))
            {
                lot.Parameters.AddWithValue("id", lotId);
                lot.Parameters.AddWithValue("number", lotNumber);
                lot.Parameters.AddWithValue("product", ordered.ProductId);
                lot.Parameters.AddWithValue("expiry", (object?)receipt.ExpiryDate ?? DBNull.Value);
                lot.Parameters.AddWithValue("received", (object?)receipt.ReceivedDate ?? DateOnly.FromDateTime(DateTime.Today));
                lot.Parameters.AddWithValue("reference", (object?)receipt.SupplierReference ?? DBNull.Value);
                lot.Parameters.AddWithValue("cost", ordered.UnitCost);
                await lot.ExecuteNonQueryAsync(ct);
            }
            await using (var entry = new NpgsqlCommand("""
                INSERT INTO inventory.lot_entry
                    (id,stock_level_id,lot_id,on_hand_value,on_hand_unit_type,reserved_value,reserved_unit_type)
                VALUES(@id,@stock,@lot,@quantity,@unit,0,@unit)
                """, connection, transaction))
            {
                entry.Parameters.AddWithValue("id", Guid.NewGuid());
                entry.Parameters.AddWithValue("stock", stockLevelId);
                entry.Parameters.AddWithValue("lot", lotId);
                entry.Parameters.AddWithValue("quantity", receipt.QuantityValue);
                entry.Parameters.AddWithValue("unit", receiptUnit);
                await entry.ExecuteNonQueryAsync(ct);
            }
        }

        await using (var received = new NpgsqlCommand(
            "UPDATE procurement.purchase_order SET status='RECEIVED' WHERE id=@id AND status='PLACED'", connection, transaction))
        {
            received.Parameters.AddWithValue("id", id);
            if (await received.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Purchase order status changed before receipt could be committed.");
        }
        var totalCost = orderLines.Values.Sum(line => line.Quantity * line.UnitCost);
        await OutboxWriter.Write(connection, transaction, "GoodsReceived",
            new GoodsReceivedEvent(id, supplierId, totalCost), ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<OrderView?> GetOrder(Guid id, CancellationToken ct)
    {
        var orders = await ReadOrders(id, ct);
        return orders.FirstOrDefault();
    }

    public Task<IReadOnlyList<OrderView>> ListOrders(CancellationToken ct) => ReadOrders(null, ct);

    private async Task<IReadOnlyList<OrderView>> ReadOrders(Guid? id, CancellationToken ct)
    {
        const string sql = """
            SELECT po.id,po.supplier_id,s.name,po.status,po.placed_at,
                   pol.id,pol.product_id,pol.value,pol.unit_type,pol.unit_cost
            FROM procurement.purchase_order po
            LEFT JOIN procurement.supplier s ON s.id=po.supplier_id
            LEFT JOIN procurement.purchase_order_line pol ON pol.purchase_order_id=po.id
            WHERE (@id IS NULL OR po.id=@id)
            ORDER BY po.placed_at DESC, pol.id
            """;
        var builders = new Dictionary<Guid, OrderBuilder>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", (object?)id ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var orderId = reader.GetGuid(0);
            if (!builders.TryGetValue(orderId, out var builder))
            {
                builder = new OrderBuilder(orderId, reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3), new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)));
                builders.Add(orderId, builder);
            }
            if (!reader.IsDBNull(5))
            {
                var quantity = reader.GetDecimal(7);
                var cost = reader.GetDecimal(9);
                builder.Lines.Add(new OrderLineView(reader.GetGuid(5), reader.GetGuid(6), quantity.ToString(),
                    reader.GetInt16(8) == 0 ? "EACH" : "WEIGHT", cost.ToString()));
                builder.Total += quantity * cost;
            }
        }
        return builders.Values.Select(x => x.ToView()).ToArray();
    }

    public async Task<IReadOnlyList<AlertView>> ListAlerts(CancellationToken ct)
    {
        const string sql = """
            SELECT a.id,a.product_id,COALESCE(p.name,'Unknown product'),
                   COALESCE(SUM(le.on_hand_value-le.reserved_value),0),COALESCE(p.reorder_point,0),a.raised_at,a.acknowledged
            FROM procurement.low_stock_alert a
            LEFT JOIN inventory.product p ON p.id=a.product_id
            LEFT JOIN inventory.stock_level sl ON sl.product_id=a.product_id
            LEFT JOIN inventory.lot_entry le ON le.stock_level_id=sl.id
            GROUP BY a.id,a.product_id,p.name,p.reorder_point,a.raised_at,a.acknowledged
            ORDER BY a.raised_at DESC
            """;
        var alerts = new List<AlertView>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var created = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc));
            alerts.Add(new AlertView(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                (double)reader.GetDecimal(3), reader.GetInt32(4), JavaInstantFormat.Format(created), reader.GetBoolean(6)));
        }
        return alerts;
    }

    public async Task AcknowledgeAlert(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("UPDATE procurement.low_stock_alert SET acknowledged=1 WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", id);
        if (await command.ExecuteNonQueryAsync(ct) == 0) throw new ArgumentException($"Alert not found: {id}");
    }

    private static SupplierView ReadSupplier(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4));

    private static void AddSupplierParameters(NpgsqlCommand command, Guid id, SupplierRequest request)
    {
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", request.Name.Trim());
        command.Parameters.AddWithValue("email", (object?)request.ContactEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("phone", (object?)request.Phone ?? DBNull.Value);
        command.Parameters.AddWithValue("address", (object?)request.Address ?? DBNull.Value);
    }

    private static void ValidateSupplier(SupplierRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Supplier name is required.");
        if (request.Name.Trim().Length > 255) throw new ArgumentException("Supplier name is too long.");
        if (request.Address?.Length > 500) throw new ArgumentException("Address cannot exceed 500 characters.");
    }

    private static void ValidateQuantity(decimal value, string unitType)
    {
        if (value <= 0) throw new ArgumentException("Quantity must be positive.");
        if (!unitType.Equals("EACH", StringComparison.OrdinalIgnoreCase)
            && !unitType.Equals("WEIGHT", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Unit type must be EACH or WEIGHT.");
        if (unitType.Equals("EACH", StringComparison.OrdinalIgnoreCase) && value != decimal.Truncate(value))
            throw new ArgumentException("EACH quantity must be a whole number.");
        if (unitType.Equals("WEIGHT", StringComparison.OrdinalIgnoreCase) && decimal.Round(value, 3) != value)
            throw new ArgumentException("WEIGHT quantities support at most three decimal places.");
    }

    private sealed class OrderBuilder(Guid id, Guid supplierId, string? supplierName, string status, DateTimeOffset placedAt)
    {
        public List<OrderLineView> Lines { get; } = [];
        public decimal Total { get; set; }
        public OrderView ToView() => new(id, supplierId, supplierName, status, Lines,
            Total.ToString(System.Globalization.CultureInfo.InvariantCulture), null,
            JavaInstantFormat.Format(placedAt), null);
    }
}
