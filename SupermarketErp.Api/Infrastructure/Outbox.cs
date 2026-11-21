using System.Text.Json;

namespace SupermarketErp.Api.Infrastructure;

public sealed record SaleCompletedEvent(Guid TransactionId, Guid CashSessionId, string PaymentMethod,
    decimal NetAmount, decimal VatAmount, decimal GrossAmount, decimal CostOfGoodsSold);
public sealed record CashSessionClosedEvent(Guid CashSessionId, decimal ExpectedCash, decimal CountedCash);
public sealed record GoodsReceivedEvent(Guid PurchaseOrderId, Guid SupplierId, decimal TotalCost);

/// <summary>
/// Reacts to one type of outbox event. Each module registers the handlers for the events it cares about, so the
/// outbox itself knows nothing about finance or procurement. A handler runs inside the processor's transaction
/// (its changes and the "processed" mark commit together) and may be called again for the same event, so it must
/// be idempotent. Throwing makes the processor retry with backoff and finally dead-letter the event.
/// </summary>
public interface IOutboxHandler
{
    string EventType { get; }

    Task Handle(string payload, CancellationToken ct);
}

/// <summary>A handler for an event whose payload is JSON of type <typeparamref name="T"/>.</summary>
public abstract class OutboxHandler<T>(string eventType) : IOutboxHandler
{
    public string EventType { get; } = eventType;

    public Task Handle(string payload, CancellationToken ct) =>
        Handle(JsonSerializer.Deserialize<T>(payload) ?? throw new JsonException($"The {EventType} payload is empty."), ct);

    protected abstract Task Handle(T @event, CancellationToken ct);
}

public static class OutboxWriter
{
    public static async Task Write<T>(BusinessDbContext db, string eventType, T payload, CancellationToken ct)
    {
        db.DotnetOutbox.Add(new DotnetOutboxEntity
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            Payload = JsonSerializer.Serialize(payload),
            OccurredAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }
}
