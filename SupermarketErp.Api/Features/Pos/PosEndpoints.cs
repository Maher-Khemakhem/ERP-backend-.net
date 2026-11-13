using System.Globalization;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using SupermarketErp.Api.Features.Auth;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Pos;

public sealed record CreateTillRequest(string TillNumber);
public sealed record OpenSessionRequest(Guid TillId, string CashierName, decimal OpeningFloat);
public sealed record CloseSessionRequest(decimal CountedCash);
public sealed record StartTransactionRequest(Guid CashSessionId);
public sealed record AddLineRequest(Guid ProductId, decimal QuantityValue, string UnitType);
public sealed record AddLineResponse(string LinePrice, string NetPrice, string VatAmount, string LineId);
public sealed record ScannerPairingView(Guid SessionId, string Token, string ExpiresAt);
public sealed record ScannerScanRequest(string Barcode);
public sealed record ScannerScanResult(string Barcode, Guid ProductId, string Sku, string ProductName,
    string UnitType, string Quantity, string LinePrice, string NetPrice, string VatAmount);
public sealed record CompleteTransactionRequest(string PaymentMethod, decimal? AmountReceived);
public sealed record VoidTransactionRequest(string? Reason);
public sealed record TillView(Guid Id, string TillNumber, string Status, string? CashierName,
    string CashInDrawer, string? OpenedAt, string? ClosedAt);
public sealed record SessionView(Guid Id, Guid TillId, string Status, string ExpectedCash);
public sealed record PosLineView(Guid Id, Guid ProductId, string Quantity, string LineTotal, string NetTotal, string VatAmount);
public sealed record TransactionView(Guid Id, Guid CashSessionId, string Status, string Total,
    string NetTotal, string VatTotal, string? PaymentMethod, string StartedAt, string? CompletedAt,
    IReadOnlyList<PosLineView> Lines);

public static class PosEndpoints
{
    public static IEndpointRouteBuilder MapPosEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var pos = endpoints.MapGroup("/api/pos").WithTags("Point of sale");
        pos.MapGet("/tills", async (PosRepository repo, CancellationToken ct) => Results.Ok(await repo.ListTills(ct)))
            .RequireAuthorization(Policies.StaffRead);
        pos.MapGet("/tills/{id:guid}", async (Guid id, PosRepository repo, CancellationToken ct) =>
            await repo.GetTill(id, ct) is { } value ? Results.Ok(value) : Results.NotFound())
            .RequireAuthorization(Policies.StaffRead);
        pos.MapPost("/tills", async (CreateTillRequest request, PosRepository repo, CancellationToken ct) =>
            Results.Ok(new { id = await repo.CreateTill(request, ct) }))
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);
        pos.MapPost("/sessions/open", async (OpenSessionRequest request, PosRepository repo, CancellationToken ct) =>
            Results.Ok(new { cashSessionId = await repo.OpenSession(request, ct) }))
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);
        pos.MapGet("/sessions/active", async (Guid tillId, PosRepository repo, CancellationToken ct) =>
            await repo.GetActiveSession(tillId, ct) is { } session ? Results.Ok(session) : Results.NoContent())
            .RequireAuthorization(Policies.StaffRead);
        pos.MapGet("/sessions/{cashSessionId:guid}", async (Guid cashSessionId, PosRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.GetSession(cashSessionId, ct)))
            .RequireAuthorization(Policies.StaffRead);
        pos.MapPost("/sessions/{cashSessionId:guid}/close", async (Guid cashSessionId, CloseSessionRequest request, PosRepository repo, CancellationToken ct) =>
        {
            await repo.CloseSession(cashSessionId, request, ct);
            return Results.Ok();
        })
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);

        pos.MapGet("/transactions", async (PosRepository repo, CancellationToken ct) => Results.Ok(await repo.ListTransactions(ct)))
            .RequireAuthorization(Policies.StaffRead);
        pos.MapPost("/transactions/start", async (StartTransactionRequest request, PosRepository repo, CancellationToken ct) =>
            Results.Ok(new { transactionId = await repo.StartTransaction(request, ct) }))
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);
        pos.MapGet("/transactions/{id:guid}", async (Guid id, PosRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.GetTransaction(id, ct)))
            .RequireAuthorization(Policies.StaffRead);
        pos.MapPost("/transactions/{id:guid}/lines", async (Guid id, AddLineRequest request, PosRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.AddLine(id, request, ct)))
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);
        pos.MapDelete("/transactions/{id:guid}/lines/{lineId:guid}", async (Guid id, Guid lineId, PosRepository repo, CancellationToken ct) =>
        {
            if (!await repo.RemoveLine(id, lineId, ct)) throw new ArgumentException($"Line not found: {lineId}");
            return Results.Ok();
        })
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);
        pos.MapPost("/transactions/{id:guid}/complete", async (Guid id, CompleteTransactionRequest request, PosRepository repo, CancellationToken ct) =>
        {
            await repo.Complete(id, request, ct);
            return Results.Ok();
        }).RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);
        pos.MapPost("/transactions/{id:guid}/void", async (Guid id, VoidTransactionRequest request, PosRepository repo, CancellationToken ct) =>
        {
            await repo.Void(id, request, ct);
            return Results.Ok();
        })
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);
        pos.MapDelete("/transactions/{id:guid}", async (Guid id, PosRepository repo, CancellationToken ct) =>
        {
            await repo.DeleteTransaction(id, ct);
            return Results.Ok();
        })
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);

        pos.MapPost("/transactions/{id:guid}/scanner-session", async (Guid id, ScannerPairingService scanner, CancellationToken ct) =>
            Results.Ok(await scanner.Create(id, ct)))
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);
        pos.MapPost("/scanner-sessions/{sessionId:guid}/scans", async (Guid sessionId, ScannerScanRequest request,
            HttpRequest http, ScannerPairingService scanner, CancellationToken ct) =>
            Results.Ok(await scanner.Scan(sessionId, http.Headers["X-Scanner-Token"].FirstOrDefault(), request.Barcode, ct)))
            .AllowAnonymous();
        pos.MapDelete("/scanner-sessions/{sessionId:guid}", (Guid sessionId, HttpRequest http, ScannerPairingService scanner) =>
        {
            scanner.Close(sessionId, http.Headers["X-Scanner-Token"].FirstOrDefault());
            return Results.NoContent();
        }).AllowAnonymous();
        return endpoints;
    }
}

public sealed class PosRepository(SqlServerDataSource dataSource)
{
    public async Task<IReadOnlyList<TillView>> ListTills(CancellationToken ct)
    {
        var list = new List<TillView>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new SqlCommand("""
            SELECT t.id,t.till_number,t.status,s.cashier_name,
                   CASE WHEN s.status='OPEN' THEN s.expected_cash ELSE COALESCE(s.counted_cash,0) END,
                   s.opened_at,s.closed_at
            FROM pos.till t LEFT JOIN LATERAL
                (SELECT * FROM pos.cash_session cs WHERE cs.till_id=t.id ORDER BY cs.opened_at DESC LIMIT 1) s ON 1
            ORDER BY t.till_number
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(ReadTill(reader));
        return list;
    }

    public async Task<TillView?> GetTill(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new SqlCommand("""
            SELECT t.id,t.till_number,t.status,s.cashier_name,
                   CASE WHEN s.status='OPEN' THEN s.expected_cash ELSE COALESCE(s.counted_cash,0) END,
                   s.opened_at,s.closed_at
            FROM pos.till t LEFT JOIN LATERAL
                (SELECT * FROM pos.cash_session cs WHERE cs.till_id=t.id ORDER BY cs.opened_at DESC LIMIT 1) s ON 1
            WHERE t.id=@id
            """, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTill(reader) : null;
    }

    public async Task<Guid> CreateTill(CreateTillRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.TillNumber)) throw new ArgumentException("Till number is required.");
        var id = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new SqlCommand("INSERT INTO pos.till(id,till_number,status,version) VALUES(@id,@number,'CLOSED',0)", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("number", request.TillNumber.Trim());
        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<Guid> OpenSession(OpenSessionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CashierName)) throw new ArgumentException("Cashier name is required.");
        if (request.OpeningFloat < 0) throw new ArgumentException("Opening float cannot be negative.");
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        await using (var till = new SqlCommand("SELECT status FROM pos.till WHERE id=@id FOR UPDATE", connection, transaction))
        {
            till.Parameters.AddWithValue("id", request.TillId);
            if (await till.ExecuteScalarAsync(ct) is null) throw new ArgumentException($"Till not found: {request.TillId}");
        }
        await using (var check = new SqlCommand("SELECT EXISTS(SELECT 1 FROM pos.cash_session WHERE till_id=@id AND status='OPEN')", connection, transaction))
        {
            check.Parameters.AddWithValue("id", request.TillId);
            if ((bool)(await check.ExecuteScalarAsync(ct) ?? false)) throw new InvalidOperationException("Till already has an open cash session.");
        }
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var insert = new SqlCommand("""
            INSERT INTO pos.cash_session(id,till_id,cashier_name,opening_float,expected_cash,counted_cash,status,opened_at,closed_at,version)
            VALUES(@id,@till,@cashier,@float,@float,NULL,'OPEN',@now,NULL,0)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("till", request.TillId);
            insert.Parameters.AddWithValue("cashier", request.CashierName.Trim());
            insert.Parameters.AddWithValue("float", request.OpeningFloat);
            insert.Parameters.AddWithValue("now", now);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using (var update = new SqlCommand("UPDATE pos.till SET status='OPEN',version=version+1 WHERE id=@id", connection, transaction))
        {
            update.Parameters.AddWithValue("id", request.TillId);
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return id;
    }

    public async Task<SessionView> GetSession(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new SqlCommand("SELECT id,till_id,status,expected_cash FROM pos.cash_session WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSession(reader) : throw new ArgumentException($"Cash session not found: {id}");
    }

    public async Task<SessionView?> GetActiveSession(Guid tillId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new SqlCommand("SELECT id,till_id,status,expected_cash FROM pos.cash_session WHERE till_id=@id AND status='OPEN' ORDER BY opened_at DESC LIMIT 1", connection);
        command.Parameters.AddWithValue("id", tillId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSession(reader) : null;
    }

    public async Task<bool> CloseSession(Guid id, CloseSessionRequest request, CancellationToken ct)
    {
        if (request.CountedCash < 0) throw new ArgumentException("Counted cash cannot be negative.");
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        Guid tillId;
        decimal expectedCash;
        await using (var session = new SqlCommand("SELECT till_id,status FROM pos.cash_session WHERE id=@id FOR UPDATE", connection, transaction))
        {
            session.Parameters.AddWithValue("id", id);
            await using var reader = await session.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new ArgumentException($"Cash session not found: {id}");
            var status = reader.GetString(1);
            if (status != "OPEN") throw new InvalidOperationException($"Cash session is not open: {id}");
            tillId = reader.GetGuid(0);
            await reader.CloseAsync();
            await using var expected = new SqlCommand("SELECT expected_cash FROM pos.cash_session WHERE id=@id", connection, transaction);
            expected.Parameters.AddWithValue("id", id);
            expectedCash = (decimal)(await expected.ExecuteScalarAsync(ct) ?? 0m);
        }
        await using (var close = new SqlCommand("UPDATE pos.cash_session SET counted_cash=@cash,status='CLOSED',closed_at=@now,version=version+1 WHERE id=@id", connection, transaction))
        {
            close.Parameters.AddWithValue("cash", request.CountedCash);
            close.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
            close.Parameters.AddWithValue("id", id);
            await close.ExecuteNonQueryAsync(ct);
        }
        await using (var updateTill = new SqlCommand("UPDATE pos.till SET status='CLOSED',version=version+1 WHERE id=@id", connection, transaction))
        {
            updateTill.Parameters.AddWithValue("id", tillId);
            await updateTill.ExecuteNonQueryAsync(ct);
        }
        await OutboxWriter.Write(connection, transaction, "CashSessionClosed",
            new CashSessionClosedEvent(id, expectedCash, request.CountedCash), ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<Guid> StartTransaction(StartTransactionRequest request, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        await using (var session = new SqlCommand("SELECT status FROM pos.cash_session WHERE id=@id FOR UPDATE", connection, transaction))
        {
            session.Parameters.AddWithValue("id", request.CashSessionId);
            if (await session.ExecuteScalarAsync(ct) as string != "OPEN") throw new InvalidOperationException("Cash session is not open.");
        }
        await using var insert = new SqlCommand("INSERT INTO pos.pos_transaction(id,cash_session_id,status,payment_method,started_at,completed_at,version) VALUES(@id,@session,'IN_PROGRESS',NULL,@now,NULL,0)", connection, transaction);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("session", request.CashSessionId);
        insert.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        await insert.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return id;
    }

    public async Task<IReadOnlyList<TransactionView>> ListTransactions(CancellationToken ct)
    {
        var ids = new List<Guid>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new SqlCommand("SELECT id FROM pos.pos_transaction ORDER BY started_at DESC", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        var list = new List<TransactionView>(ids.Count);
        foreach (var id in ids) if (await GetTransaction(id, ct) is { } ticket) list.Add(ticket);
        return list;
    }

    public async Task<TransactionView> GetTransaction(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var ticket = new SqlCommand("SELECT id,cash_session_id,status,payment_method,started_at,completed_at FROM pos.pos_transaction WHERE id=@id", connection);
        ticket.Parameters.AddWithValue("id", id);
        await using var reader = await ticket.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new ArgumentException($"Ticket not found: {id}");
        var cashSessionId = reader.GetGuid(1);
        var status = reader.GetString(2);
        var method = reader.IsDBNull(3) ? null : reader.GetString(3);
        var started = JavaInstantFormat.Format(AsInstant(reader.GetDateTime(4)));
        var completed = reader.IsDBNull(5) ? null : JavaInstantFormat.Format(AsInstant(reader.GetDateTime(5)));
        await reader.CloseAsync();
        var lines = await ReadLines(id, connection, ct);
        var total = lines.Sum(x => ParseMoney(x.LineTotal));
        var vat = lines.Sum(x => ParseMoney(x.VatAmount));
        return new TransactionView(id, cashSessionId, status, FormatMoney(total), FormatMoney(total - vat),
            FormatMoney(vat), method, started, completed, lines);
    }

    public async Task<AddLineResponse> AddLine(Guid transactionId, AddLineRequest request, CancellationToken ct)
    {
        ValidateQuantity(request.QuantityValue, request.UnitType);
        var unit = request.UnitType.Trim().ToUpperInvariant();
        var quantityValue = unit == "WEIGHT"
            ? decimal.Round(request.QuantityValue, 3, MidpointRounding.AwayFromZero)
            : request.QuantityValue;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        Guid sessionId;
        await using (var ticket = new SqlCommand("SELECT cash_session_id FROM pos.pos_transaction WHERE id=@id AND status='IN_PROGRESS' FOR UPDATE", connection, transaction))
        {
            ticket.Parameters.AddWithValue("id", transactionId);
            var value = await ticket.ExecuteScalarAsync(ct);
            if (value is null) throw new ArgumentException("Ticket not found or is not in progress.");
            sessionId = (Guid)value;
        }
        await RequireOpenSession(connection, transaction, sessionId, ct);

        decimal vatRate;
        string productUnit;
        await using (var product = new SqlCommand("SELECT unit_type,vat_rate FROM inventory.product WHERE id=@id", connection, transaction))
        {
            product.Parameters.AddWithValue("id", request.ProductId);
            await using var reader = await product.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new ArgumentException($"Product not found: {request.ProductId}");
            productUnit = reader.GetString(0);
            vatRate = reader.IsDBNull(1) ? 19m : reader.GetDecimal(1);
        }
        if (!productUnit.Equals(unit, StringComparison.Ordinal)) throw new ArgumentException("Requested unit type does not match the product.");

        decimal unitPrice;
        await using (var price = new SqlCommand("SELECT unit_price FROM pricing.product_price WHERE product_id=@id", connection, transaction))
        {
            price.Parameters.AddWithValue("id", request.ProductId);
            var result = await price.ExecuteScalarAsync(ct);
            if (result is not decimal value) throw new ArgumentException($"No price defined for product: {request.ProductId}");
            unitPrice = value;
        }
        decimal net = decimal.Round(unitPrice * quantityValue, 3, MidpointRounding.AwayFromZero);
        await using (var promo = new SqlCommand("""
            SELECT discount_percent FROM pricing.promotion
            WHERE product_id=@id AND active=1 AND valid_from<=@now AND valid_until>=@now
            ORDER BY valid_from LIMIT 1
            """, connection, transaction))
        {
            promo.Parameters.AddWithValue("id", request.ProductId);
            promo.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
            var discount = await promo.ExecuteScalarAsync(ct);
            if (discount is decimal percent)
            {
                var discountFraction = decimal.Round(percent / 100m, 4, MidpointRounding.AwayFromZero);
                net = decimal.Round(net * (1m - discountFraction), 3, MidpointRounding.AwayFromZero);
            }
        }
        var vat = decimal.Round(net * vatRate / 100m, 3, MidpointRounding.AwayFromZero);
        var gross = net + vat;
        var lineId = Guid.NewGuid();
        await using (var insert = new SqlCommand("""
            INSERT INTO pos.transaction_line(id,transaction_id,product_id,value,unit_type,line_total,vat_amount)
            VALUES(@id,@transaction,@product,@quantity,@unit,@total,@vat)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", lineId);
            insert.Parameters.AddWithValue("transaction", transactionId);
            insert.Parameters.AddWithValue("product", request.ProductId);
            insert.Parameters.AddWithValue("quantity", quantityValue);
            insert.Parameters.AddWithValue("unit", unit == "EACH" ? (short)0 : (short)1);
            insert.Parameters.AddWithValue("total", gross);
            insert.Parameters.AddWithValue("vat", vat);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using (var version = new SqlCommand("UPDATE pos.pos_transaction SET version=version+1 WHERE id=@id", connection, transaction))
        {
            version.Parameters.AddWithValue("id", transactionId);
            await version.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new AddLineResponse(FormatMoney(gross), FormatMoney(net), FormatMoney(vat), lineId.ToString());
    }

    public async Task<bool> RemoveLine(Guid transactionId, Guid lineId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        await RequireInProgress(connection, transaction, transactionId, ct);
        await using var delete = new SqlCommand("DELETE FROM pos.transaction_line WHERE transaction_id=@transaction AND id=@line", connection, transaction);
        delete.Parameters.AddWithValue("transaction", transactionId);
        delete.Parameters.AddWithValue("line", lineId);
        var changed = await delete.ExecuteNonQueryAsync(ct) != 0;
        if (changed)
        {
            await using var version = new SqlCommand("UPDATE pos.pos_transaction SET version=version+1 WHERE id=@id", connection, transaction);
            version.Parameters.AddWithValue("id", transactionId);
            await version.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return changed;
    }

    public async Task Complete(Guid id, CompleteTransactionRequest request, CancellationToken ct)
    {
        var method = request.PaymentMethod.Trim().ToUpperInvariant();
        if (method is not ("CASH" or "CARD")) throw new ArgumentException("Payment method must be CASH or CARD.");
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        Guid sessionId;
        await using (var ticket = new SqlCommand("SELECT cash_session_id FROM pos.pos_transaction WHERE id=@id AND status='IN_PROGRESS' FOR UPDATE", connection, transaction))
        {
            ticket.Parameters.AddWithValue("id", id);
            var value = await ticket.ExecuteScalarAsync(ct);
            if (value is null) throw new InvalidOperationException("Only in-progress tickets can be completed.");
            sessionId = (Guid)value;
        }
        await RequireOpenSession(connection, transaction, sessionId, ct);
        decimal total;
        await using (var amount = new SqlCommand("SELECT COALESCE(SUM(line_total),0) FROM pos.transaction_line WHERE transaction_id=@id", connection, transaction))
        {
            amount.Parameters.AddWithValue("id", id);
            total = (decimal)(await amount.ExecuteScalarAsync(ct) ?? 0m);
        }
        if (request.AmountReceived is decimal tender
            && ((method == "CASH" && tender < total) || (method == "CARD" && tender != total)))
            throw new ArgumentException(method == "CASH" ? "Cash received is less than the transaction total." : "Card amount must equal the transaction total.");

        var lines = new List<(Guid Product, decimal Quantity, short Unit)>();
        await using (var query = new SqlCommand("SELECT product_id,value,unit_type FROM pos.transaction_line WHERE transaction_id=@id", connection, transaction))
        {
            query.Parameters.AddWithValue("id", id);
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) lines.Add((reader.GetGuid(0), reader.GetDecimal(1), reader.GetInt16(2)));
        }
        decimal cogs = 0;
        foreach (var line in lines) cogs += await SellFromStock(connection, transaction, line.Product, line.Quantity, line.Unit, ct);
        decimal netAmount;
        decimal vatAmount;
        await using (var totals = new SqlCommand("SELECT COALESCE(SUM(line_total-vat_amount),0),COALESCE(SUM(vat_amount),0) FROM pos.transaction_line WHERE transaction_id=@id", connection, transaction))
        {
            totals.Parameters.AddWithValue("id", id);
            await using var reader = await totals.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            netAmount = reader.GetDecimal(0);
            vatAmount = reader.GetDecimal(1);
        }
        await using (var complete = new SqlCommand("UPDATE pos.pos_transaction SET status='COMPLETED',payment_method=@method,completed_at=@now,version=version+1 WHERE id=@id", connection, transaction))
        {
            complete.Parameters.AddWithValue("method", method);
            complete.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
            complete.Parameters.AddWithValue("id", id);
            await complete.ExecuteNonQueryAsync(ct);
        }
        if (method == "CASH")
        {
            await using var cash = new SqlCommand("UPDATE pos.cash_session SET expected_cash=expected_cash+@total,version=version+1 WHERE id=@id", connection, transaction);
            cash.Parameters.AddWithValue("total", total);
            cash.Parameters.AddWithValue("id", sessionId);
            await cash.ExecuteNonQueryAsync(ct);
        }
        await OutboxWriter.Write(connection, transaction, "SaleCompleted",
            new SaleCompletedEvent(id, sessionId, method, netAmount, vatAmount, total, cogs), ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<bool> Void(Guid id, VoidTransactionRequest request, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        await RequireInProgress(connection, transaction, id, ct);
        var session = await GetTransactionSession(connection, transaction, id, ct);
        await RequireOpenSession(connection, transaction, session, ct);
        await using var command = new SqlCommand("UPDATE pos.pos_transaction SET status='VOIDED',version=version+1 WHERE id=@id", connection, transaction);
        command.Parameters.AddWithValue("id", id);
        var changed = await command.ExecuteNonQueryAsync(ct) != 0;
        await transaction.CommitAsync(ct);
        return changed;
    }

    public async Task<bool> DeleteTransaction(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        await RequireInProgress(connection, transaction, id, ct);
        var session = await GetTransactionSession(connection, transaction, id, ct);
        await RequireOpenSession(connection, transaction, session, ct);
        await using (var lines = new SqlCommand("DELETE FROM pos.transaction_line WHERE transaction_id=@id", connection, transaction))
        {
            lines.Parameters.AddWithValue("id", id);
            await lines.ExecuteNonQueryAsync(ct);
        }
        await using var command = new SqlCommand("DELETE FROM pos.pos_transaction WHERE id=@id", connection, transaction);
        command.Parameters.AddWithValue("id", id);
        var changed = await command.ExecuteNonQueryAsync(ct) != 0;
        await transaction.CommitAsync(ct);
        return changed;
    }

    private static async Task<decimal> SellFromStock(SqlConnection connection, SqlTransaction tx, Guid productId,
        decimal requested, short unit, CancellationToken ct)
    {
        await using var stock = new SqlCommand("SELECT id FROM inventory.stock_level WHERE product_id=@id FOR UPDATE", connection, tx);
        stock.Parameters.AddWithValue("id", productId);
        var stockId = await stock.ExecuteScalarAsync(ct);
        if (stockId is null) throw new InvalidOperationException($"No stock found for product: {productId}");
        var lots = new List<(Guid EntryId, decimal OnHand, decimal Reserved, decimal UnitCost)>();
        await using (var query = new SqlCommand("""
            SELECT le.id,le.on_hand_value,le.reserved_value,l.unit_cost
            FROM inventory.lot_entry le JOIN inventory.lot l ON l.id=le.lot_id
            WHERE le.stock_level_id=@id AND le.on_hand_unit_type=@unit
            ORDER BY l.expiry_date NULLS LAST,l.received_date,l.id FOR UPDATE OF le
            """, connection, tx))
        {
            query.Parameters.AddWithValue("id", (Guid)stockId);
            query.Parameters.AddWithValue("unit", unit);
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) lots.Add((reader.GetGuid(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3)));
        }
        var available = lots.Sum(x => x.OnHand - x.Reserved);
        if (available < requested) throw new InvalidOperationException($"Insufficient stock for product {productId}: requested {requested}, available {available}.");
        var remaining = requested;
        decimal cost = 0;
        foreach (var lot in lots)
        {
            if (remaining <= 0) break;
            var take = Math.Min(remaining, lot.OnHand - lot.Reserved);
            if (take <= 0) continue;
            cost = decimal.Round(cost + take * lot.UnitCost, 3, MidpointRounding.AwayFromZero);
            await using var update = new SqlCommand("UPDATE inventory.lot_entry SET on_hand_value=on_hand_value-@quantity WHERE id=@id", connection, tx);
            update.Parameters.AddWithValue("quantity", take);
            update.Parameters.AddWithValue("id", lot.EntryId);
            await update.ExecuteNonQueryAsync(ct);
            remaining -= take;
        }
        await using var version = new SqlCommand("UPDATE inventory.stock_level SET version=version+1 WHERE id=@id", connection, tx);
        version.Parameters.AddWithValue("id", (Guid)stockId);
        await version.ExecuteNonQueryAsync(ct);
        await using var product = new SqlCommand("SELECT COALESCE(p.reorder_point,0),COALESCE(SUM(le.on_hand_value-le.reserved_value),0) FROM inventory.product p LEFT JOIN inventory.stock_level sl ON sl.product_id=p.id LEFT JOIN inventory.lot_entry le ON le.stock_level_id=sl.id WHERE p.id=@id GROUP BY p.id", connection, tx);
        product.Parameters.AddWithValue("id", productId);
        await using var productReader = await product.ExecuteReaderAsync(ct);
        if (await productReader.ReadAsync(ct) && productReader.GetDecimal(1) <= productReader.GetInt32(0))
        {
            await productReader.CloseAsync();
            await OutboxWriter.Write(connection, tx, "LowStockThresholdReached", productId, ct);
        }
        return cost;
    }

    private static async Task<IReadOnlyList<PosLineView>> ReadLines(Guid transactionId, SqlConnection connection, CancellationToken ct)
    {
        var lines = new List<PosLineView>();
        await using var command = new SqlCommand("SELECT id,product_id,value,unit_type,line_total,vat_amount FROM pos.transaction_line WHERE transaction_id=@id ORDER BY id", connection);
        command.Parameters.AddWithValue("id", transactionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var quantity = reader.GetDecimal(2);
            var unit = reader.GetInt16(3) == 0 ? "EACH" : "WEIGHT";
            lines.Add(new PosLineView(reader.GetGuid(0), reader.GetGuid(1),
                unit == "EACH" ? $"{quantity:0} unit(s)" : $"{quantity:0.000} kg",
                FormatMoney(reader.GetDecimal(4)), FormatMoney(reader.GetDecimal(4) - reader.GetDecimal(5)), FormatMoney(reader.GetDecimal(5))));
        }
        return lines;
    }

    private static async Task RequireInProgress(SqlConnection c, SqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT status FROM pos.pos_transaction WHERE id=@id FOR UPDATE", c, tx);
        command.Parameters.AddWithValue("id", id);
        var status = await command.ExecuteScalarAsync(ct) as string ?? throw new ArgumentException($"Ticket not found: {id}");
        if (status != "IN_PROGRESS") throw new InvalidOperationException("Only in-progress tickets can be changed.");
    }

    private static async Task<Guid> GetTransactionSession(SqlConnection c, SqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT cash_session_id FROM pos.pos_transaction WHERE id=@id", c, tx);
        command.Parameters.AddWithValue("id", id);
        return (Guid)(await command.ExecuteScalarAsync(ct) ?? throw new ArgumentException("Ticket not found."));
    }

    private static async Task RequireOpenSession(SqlConnection c, SqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT status FROM pos.cash_session WHERE id=@id FOR UPDATE", c, tx);
        command.Parameters.AddWithValue("id", id);
        var status = await command.ExecuteScalarAsync(ct) as string ?? throw new ArgumentException($"Cash session not found: {id}");
        if (status != "OPEN") throw new InvalidOperationException("Cash session is not open.");
    }

    private static TillView ReadTill(SqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3), FormatMoney(r.GetDecimal(4)),
        r.IsDBNull(5) ? null : JavaInstantFormat.Format(AsInstant(r.GetDateTime(5))),
        r.IsDBNull(6) ? null : JavaInstantFormat.Format(AsInstant(r.GetDateTime(6))));

    private static SessionView ReadSession(SqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), FormatMoney(r.GetDecimal(3)));

    private static void ValidateQuantity(decimal value, string unitType)
    {
        var unit = unitType.Trim().ToUpperInvariant();
        if (value <= 0) throw new ArgumentException("Quantity must be positive.");
        if (unit is not ("EACH" or "WEIGHT")) throw new ArgumentException("Unit type must be EACH or WEIGHT.");
        if (unit == "EACH" && value != decimal.Truncate(value)) throw new ArgumentException("EACH quantity must be a whole number.");
        // The Java Quantity value object rounds weighted quantities to 3 decimals using HALF_UP.
    }

    private static string FormatMoney(decimal amount) => $"{amount.ToString("0.000", CultureInfo.InvariantCulture)} TND";
    private static decimal ParseMoney(string value) => decimal.Parse(value[..^4], CultureInfo.InvariantCulture);
    private static DateTimeOffset AsInstant(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

public sealed class ScannerPairingService(SqlServerDataSource dataSource, PosRepository pos)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<Guid, Pairing> _sessions = new();

    public async Task<ScannerPairingView> Create(Guid transactionId, CancellationToken ct)
    {
        var transaction = await pos.GetTransaction(transactionId, ct)
            ?? throw new ArgumentException($"Ticket not found: {transactionId}");
        if (transaction.Status != "IN_PROGRESS") throw new InvalidOperationException("Only an in-progress ticket can use the phone scanner.");
        var now = DateTimeOffset.UtcNow;
        foreach (var existing in _sessions.Where(pair => pair.Value.ExpiresAt < now || pair.Value.TransactionId == transactionId))
            _sessions.TryRemove(existing.Key, out _);
        var id = Guid.NewGuid();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var expires = now.Add(Lifetime);
        _sessions[id] = new Pairing(id, token, transactionId, expires);
        return new ScannerPairingView(id, token, JavaInstantFormat.Format(expires));
    }

    public async Task<ScannerScanResult> Scan(Guid sessionId, string? token, string rawBarcode, CancellationToken ct)
    {
        var pairing = Require(sessionId, token);
        var transaction = await pos.GetTransaction(pairing.TransactionId, ct)
            ?? throw new ArgumentException("Scanner ticket no longer exists.");
        if (transaction.Status != "IN_PROGRESS") throw new InvalidOperationException("Only an in-progress ticket can use the phone scanner.");
        var barcode = rawBarcode?.Trim() ?? "";
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new SqlCommand("SELECT id,sku,name,unit_type FROM inventory.product WHERE lower(sku)=lower(@sku)", connection);
        command.Parameters.AddWithValue("sku", barcode);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new ArgumentException($"No product found for barcode: {barcode}");
        var productId = reader.GetGuid(0);
        var sku = reader.GetString(1);
        var name = reader.GetString(2);
        var unit = reader.GetString(3);
        await reader.CloseAsync();
        var quantity = unit == "WEIGHT" ? 0.1m : 1m;
        var line = await pos.AddLine(pairing.TransactionId, new AddLineRequest(productId, quantity, unit), ct);
        return new ScannerScanResult(barcode, productId, sku, name, unit,
            unit == "WEIGHT" ? "0.100 kg" : "1 unit(s)", line.LinePrice, line.NetPrice, line.VatAmount);
    }

    public void Close(Guid sessionId, string? token)
    {
        _ = Require(sessionId, token);
        _sessions.TryRemove(sessionId, out _);
    }

    private Pairing Require(Guid sessionId, string? token)
    {
        if (!_sessions.TryGetValue(sessionId, out var pairing) || pairing.ExpiresAt < DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(sessionId, out _);
            throw new ArgumentException("Scanner pairing has expired or does not exist.");
        }
        if (token is null || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(pairing.Token)))
            throw new ArgumentException("Invalid scanner pairing token.");
        return pairing;
    }

    private sealed record Pairing(Guid Id, string Token, Guid TransactionId, DateTimeOffset ExpiresAt);
}

