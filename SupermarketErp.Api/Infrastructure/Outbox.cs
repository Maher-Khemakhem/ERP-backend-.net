using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace SupermarketErp.Api.Infrastructure;

public sealed record SaleCompletedEvent(Guid TransactionId, Guid CashSessionId, string PaymentMethod,
    decimal NetAmount, decimal VatAmount, decimal GrossAmount, decimal CostOfGoodsSold);
public sealed record CashSessionClosedEvent(Guid CashSessionId, decimal ExpectedCash, decimal CountedCash);
public sealed record GoodsReceivedEvent(Guid PurchaseOrderId, Guid SupplierId, decimal TotalCost);

public static class OutboxWriter
{
    public static async Task Write<T>(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string eventType, T payload, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO platform.dotnet_outbox(id,event_type,payload,occurred_at)
            VALUES(@id,@type,@payload,@now)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("type", eventType);
        command.Parameters.Add(new NpgsqlParameter("payload", System.Data.SqlDbType.NVarChar)
        {
            Value = JsonSerializer.Serialize(payload)
        });
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(ct);
    }
}
