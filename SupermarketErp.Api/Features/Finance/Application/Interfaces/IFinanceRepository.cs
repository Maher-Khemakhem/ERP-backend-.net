namespace SupermarketErp.Api.Features.Finance;

/// <summary>
/// Persistence for the accounting aggregates: loads and stores entities and nothing else. The rules live in the
/// domain entities and <see cref="FinanceService"/>; saving and transactions belong to the unit of work.
/// </summary>
public interface IFinanceRepository
{
    Task<IReadOnlyList<JournalEntryEntity>> ListJournals(CancellationToken ct);
    Task<IReadOnlyList<LedgerEntryEntity>> ListLedgerEntries(CancellationToken ct);
    Task<bool> JournalExists(string eventKey, CancellationToken ct);
    void Add(JournalEntryEntity journal);

    /// <summary>Credits minus debits per account for journals posted in [from, to).</summary>
    Task<IReadOnlyDictionary<string, decimal>> AccountBalances(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    Task<IReadOnlyList<SupplierInvoiceEntity>> ListInvoices(CancellationToken ct);
    Task<SupplierInvoiceEntity?> FindInvoice(Guid id, CancellationToken ct);
    void Add(SupplierInvoiceEntity invoice);

    Task<SupplierPaymentEntity?> FindPayment(string reference, CancellationToken ct);
    void Add(SupplierPaymentEntity payment);
}
