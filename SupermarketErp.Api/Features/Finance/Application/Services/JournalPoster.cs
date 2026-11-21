namespace SupermarketErp.Api.Features.Finance;

/// <summary>
/// Books a balanced journal entry once per event key. It adds to the caller's transaction and never starts one, so
/// it serves both the finance use cases and the event handlers that book sales, receipts and till closes.
/// </summary>
public sealed class JournalPoster(IFinanceRepository repository)
{
    public async Task PostOnce(string eventKey, string description, Guid? reference, IReadOnlyList<JournalLineInput> lines, CancellationToken ct)
    {
        if (lines.Count == 0) return;
        var entry = JournalEntryEntity.Post(eventKey, description, reference, lines, DateTimeOffset.UtcNow);
        if (await repository.JournalExists(eventKey, ct)) return;
        repository.Add(entry);
    }
}
