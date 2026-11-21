using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Tests;

/// <summary>
/// Failure handling of the outbox processor. Kept in its own class on purpose: the failing events inserted
/// here are never delivered, so they must not share a fixture (database) with other tests.
/// </summary>
public sealed class OutboxPoisonEventTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private ErpApi Api => fixture.Api;

    [Fact]
    public async Task A_poison_event_does_not_block_the_events_behind_it()
    {
        await InsertEvent("NoSuchEvent", "{}", hoursAgo: 24);

        var transaction = await CompleteOneSale();

        await Api.WaitForJournal($"sale:{transaction}", timeoutMs: 10_000);
    }

    [Fact]
    public async Task An_event_that_can_never_succeed_is_dead_lettered_immediately()
    {
        var poison = await InsertEvent("NoSuchEvent", "{}");

        var row = await WaitForRow(poison, r => r.FailedAt is not null);

        Assert.Equal(1, row.Attempts);
        Assert.Contains("Unknown outbox event type", row.LastError);
        Assert.Null(row.ProcessedAt);
    }

    [Fact]
    public async Task A_failing_event_is_retried_later_and_does_not_hold_up_the_queue()
    {
        // An unbalanced sale makes the journal handler throw, which could be transient, so it must be retried.
        var failing = await InsertEvent("SaleCompleted", UnbalancedSale());

        var transaction = await CompleteOneSale();
        await Api.WaitForJournal($"sale:{transaction}", timeoutMs: 10_000); // processed behind the failing event

        var row = await WaitForRow(failing, r => r.Attempts >= 1);
        Assert.Null(row.FailedAt);           // not given up on yet
        Assert.NotNull(row.NextAttemptAt);   // scheduled for a later retry
        Assert.True(row.NextAttemptAt > DateTimeOffset.UtcNow.AddMilliseconds(500), "retry should be backed off");
        Assert.Contains("unbalanced", row.LastError);
    }

    [Fact]
    public async Task An_event_is_dead_lettered_once_it_has_used_all_its_attempts()
    {
        var lastChance = await InsertEvent("SaleCompleted", UnbalancedSale(), attempts: OutboxProcessor.MaxAttempts - 1);

        var row = await WaitForRow(lastChance, r => r.FailedAt is not null);

        Assert.Equal(OutboxProcessor.MaxAttempts, row.Attempts);
        Assert.Null(row.NextAttemptAt);
        Assert.Null(row.ProcessedAt);
    }

    [Fact]
    public async Task A_dead_lettered_event_can_be_requeued_and_then_succeeds()
    {
        var sale = Guid.NewGuid();
        var id = await InsertEvent("SaleCompleted", UnbalancedSale(sale), attempts: OutboxProcessor.MaxAttempts - 1);
        await WaitForRow(id, r => r.FailedAt is not null);

        // An operator repairs the data (here: the payload) and re-queues the event.
        await fixture.ExecuteAsync(
            "UPDATE platform.dotnet_outbox SET payload = @payload, failed_at = NULL, next_attempt_at = NULL, attempts = 0 WHERE id = @id",
            ("payload", BalancedSale(sale)), ("id", id));

        await Api.WaitForJournal($"sale:{sale}", timeoutMs: 10_000);
    }

    private async Task<Guid> CompleteOneSale()
    {
        var product = await Api.ProductWithStock(price: 10m, quantity: 10, unitCost: 4m);
        var session = await Api.OpenSession();
        var transaction = await Api.StartTransaction(session);
        await Api.AddLine(transaction, product, 1);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await Api.Complete(transaction, "CARD")).Status);
        return transaction;
    }

    private static string UnbalancedSale(Guid? transaction = null) => SaleJson(transaction ?? Guid.NewGuid(), gross: 5m);
    private static string BalancedSale(Guid transaction) => SaleJson(transaction, gross: 11m);

    // net 10 + VAT 1 must equal gross for the journal to balance
    private static string SaleJson(Guid transaction, decimal gross) =>
        $$"""{"TransactionId":"{{transaction}}","CashSessionId":"{{Guid.NewGuid()}}","PaymentMethod":"CARD","NetAmount":10,"VatAmount":1,"GrossAmount":{{gross}},"CostOfGoodsSold":0}""";

    private async Task<Guid> InsertEvent(string type, string payload, int attempts = 0, int hoursAgo = 0)
    {
        var id = Guid.NewGuid();
        await fixture.ExecuteAsync(
            "INSERT INTO platform.dotnet_outbox(id, event_type, payload, occurred_at, attempts) " +
            "VALUES (@id, @type, @payload, DATEADD(HOUR, -@hours, SYSDATETIMEOFFSET()), @attempts)",
            ("id", id), ("type", type), ("payload", payload), ("hours", hoursAgo), ("attempts", attempts));
        return id;
    }

    private sealed record OutboxRow(int Attempts, DateTimeOffset? FailedAt, DateTimeOffset? NextAttemptAt,
        DateTimeOffset? ProcessedAt, string? LastError);

    private async Task<OutboxRow> WaitForRow(Guid id, Func<OutboxRow, bool> ready) =>
        await ErpApi.Eventually(() => ReadRow(id), ready, $"outbox row {id} to reach the expected state", timeoutMs: 15_000);

    private async Task<OutboxRow> ReadRow(Guid id)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT attempts, failed_at, next_attempt_at, processed_at, last_error FROM platform.dotnet_outbox WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new OutboxRow(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetDateTimeOffset(1),
            reader.IsDBNull(2) ? null : reader.GetDateTimeOffset(2),
            reader.IsDBNull(3) ? null : reader.GetDateTimeOffset(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }
}
