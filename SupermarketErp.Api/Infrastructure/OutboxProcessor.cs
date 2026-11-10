using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace SupermarketErp.Api.Infrastructure;

public sealed class OutboxProcessor(NpgsqlDataSource dataSource, ILogger<OutboxProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessNext(stoppingToken);
                if (!processed) await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Outbox processor loop failed");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessNext(CancellationToken ct)
    {
        Guid id;
        string type;
        string payload;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        await using (var select = new NpgsqlCommand("""
            SELECT TOP (1) id,event_type,payload FROM platform.dotnet_outbox WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE processed_at IS NULL
            ORDER BY occurred_at,id
            """, connection, transaction))
        {
            await using var reader = await select.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return false;
            id = reader.GetGuid(0);
            type = reader.GetString(1);
            payload = reader.GetString(2);
        }

        try
        {
            switch (type)
            {
                case "SaleCompleted":
                    await PostSale(connection, transaction, JsonSerializer.Deserialize<SaleCompletedEvent>(payload)!, ct);
                    break;
                case "OnlineSaleConfirmed":
                    await PostOnlineSale(connection, transaction, JsonSerializer.Deserialize<SaleCompletedEvent>(payload)!, ct);
                    break;
                case "CashSessionClosed":
                    await PostCashClose(connection, transaction, JsonSerializer.Deserialize<CashSessionClosedEvent>(payload)!, ct);
                    break;
                case "GoodsReceived":
                    await PostGoodsReceived(connection, transaction, JsonSerializer.Deserialize<GoodsReceivedEvent>(payload)!, ct);
                    break;
                case "LowStockThresholdReached":
                    await CreateLowStockAlert(connection, transaction, JsonSerializer.Deserialize<Guid>(payload), ct);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown outbox event type '{type}'.");
            }

            await using var mark = new NpgsqlCommand(
                "UPDATE platform.dotnet_outbox SET processed_at=@now,last_error=NULL WHERE id=@id", connection, transaction);
            mark.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
            mark.Parameters.AddWithValue("id", id);
            await mark.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return true;
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync(ct);
            await RecordFailure(id, exception, ct);
            return true;
        }
    }

    private async Task RecordFailure(Guid id, Exception exception, CancellationToken ct)
    {
        logger.LogError(exception, "Failed to process outbox event {OutboxId}", id);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            UPDATE platform.dotnet_outbox SET attempts=attempts+1,last_error=@error WHERE id=@id
            """, connection);
        command.Parameters.AddWithValue("error", exception.Message.Length > 4000 ? exception.Message[..4000] : exception.Message);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
    }

    private static Task PostSale(NpgsqlConnection c, NpgsqlTransaction tx, SaleCompletedEvent e, CancellationToken ct) =>
        PostSaleCore(c, tx, $"sale:{e.TransactionId}", $"POS sale {e.TransactionId}", e.TransactionId,
            e.PaymentMethod, e.NetAmount, e.VatAmount, e.GrossAmount, e.CostOfGoodsSold, ct);

    private static Task PostOnlineSale(NpgsqlConnection c, NpgsqlTransaction tx, SaleCompletedEvent e, CancellationToken ct) =>
        PostSaleCore(c, tx, $"online-sale:{e.TransactionId}", $"Online sale {e.TransactionId}", e.TransactionId,
            e.PaymentMethod, e.NetAmount, e.VatAmount, e.GrossAmount, e.CostOfGoodsSold, ct);

    private static async Task PostSaleCore(NpgsqlConnection c, NpgsqlTransaction tx, string key, string description,
        Guid reference, string method, decimal net, decimal vat, decimal gross, decimal cogs, CancellationToken ct)
    {
        var lines = new List<JournalLineInput>
        {
            new(method == "CASH" ? "CASH_ON_HAND" : "CARD_CLEARING", gross, 0, "Customer payment"),
            new("SALES_REVENUE", 0, net, "Retail sale")
        };
        if (vat != 0) lines.Add(new("OUTPUT_VAT", 0, vat, "Output VAT"));
        if (cogs != 0)
        {
            lines.Add(new("COST_OF_GOODS_SOLD", cogs, 0, "Cost of goods sold"));
            lines.Add(new("INVENTORY", 0, cogs, "Inventory consumed"));
        }
        await PostJournal(c, tx, key, description, reference, lines, ct);
    }

    private static async Task PostCashClose(NpgsqlConnection c, NpgsqlTransaction tx, CashSessionClosedEvent e, CancellationToken ct)
    {
        if (e.ExpectedCash == 0 && e.CountedCash == 0) return;
        var variance = e.CountedCash - e.ExpectedCash;
        var lines = new List<JournalLineInput>();
        if (e.CountedCash != 0) lines.Add(new("BANK", e.CountedCash, 0, "Cash deposited"));
        if (e.ExpectedCash != 0) lines.Add(new("CASH_ON_HAND", 0, e.ExpectedCash, "Close cash drawer"));
        if (variance > 0) lines.Add(new("CASH_VARIANCE", 0, variance, "Cash overage"));
        else if (variance < 0) lines.Add(new("CASH_VARIANCE", -variance, 0, "Cash shortage"));
        await PostJournal(c, tx, $"cash-close:{e.CashSessionId}", $"Cash session close {e.CashSessionId}", e.CashSessionId, lines, ct);
    }

    private static async Task PostGoodsReceived(NpgsqlConnection c, NpgsqlTransaction tx, GoodsReceivedEvent e, CancellationToken ct)
    {
        if (e.TotalCost == 0) return;
        await PostJournal(c, tx, $"goods-received:{e.PurchaseOrderId}", $"Goods received for purchase order {e.PurchaseOrderId}",
            e.PurchaseOrderId,
            [new("INVENTORY", e.TotalCost, 0, "Inventory received"),
             new("GOODS_RECEIVED_NOT_INVOICED", 0, e.TotalCost, "Uninvoiced receipt")], ct);
    }

    private static async Task CreateLowStockAlert(NpgsqlConnection c, NpgsqlTransaction tx, Guid productId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO procurement.low_stock_alert(id,product_id,raised_at,acknowledged)
            VALUES(@id,@product,@now,0)
            """, c, tx);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("product", productId);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task PostJournal(NpgsqlConnection c, NpgsqlTransaction tx, string key,
        string description, Guid? reference, IReadOnlyList<JournalLineInput> lines, CancellationToken ct)
    {
        var debits = lines.Sum(line => line.Debit);
        var credits = lines.Sum(line => line.Credit);
        if (debits != credits) throw new InvalidOperationException($"Journal entry {key} is unbalanced.");
        var entryId = Guid.NewGuid();
        await using (var entry = new NpgsqlCommand("""
            IF EXISTS (SELECT 1 FROM finance.journal_entry WHERE event_key=@key)
                SELECT CAST(NULL AS uniqueidentifier);
            ELSE
            BEGIN
                INSERT INTO finance.journal_entry(id,event_key,description,reference_id,posted_at)
                VALUES(@id,@key,@description,@reference,@now);
                SELECT @id;
            END
            """, c, tx))
        {
            entry.Parameters.AddWithValue("id", entryId);
            entry.Parameters.AddWithValue("key", key);
            entry.Parameters.AddWithValue("description", description);
            entry.Parameters.AddWithValue("reference", (object?)reference ?? DBNull.Value);
            entry.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
            var inserted = await entry.ExecuteScalarAsync(ct);
            if (inserted is null) return;
            entryId = (Guid)inserted;
        }
        foreach (var line in lines)
        {
            await using var insertLine = new NpgsqlCommand("""
                INSERT INTO finance.journal_line(id,journal_entry_id,account_code,debit_amount,credit_amount,description)
                VALUES(@id,@entry,@account,@debit,@credit,@description)
                """, c, tx);
            insertLine.Parameters.AddWithValue("id", Guid.NewGuid());
            insertLine.Parameters.AddWithValue("entry", entryId);
            insertLine.Parameters.AddWithValue("account", line.Account);
            insertLine.Parameters.AddWithValue("debit", line.Debit);
            insertLine.Parameters.AddWithValue("credit", line.Credit);
            insertLine.Parameters.AddWithValue("description", line.Description);
            await insertLine.ExecuteNonQueryAsync(ct);
        }
    }

    private sealed record JournalLineInput(string Account, decimal Debit, decimal Credit, string Description);
}
