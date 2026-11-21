using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Finance;

public sealed class FinanceRepository(BusinessDbContext db) : IFinanceRepository
{
    public async Task<IReadOnlyList<JournalEntryEntity>> ListJournals(CancellationToken ct) =>
        await db.JournalEntries.AsNoTracking().Include(x => x.Lines).OrderByDescending(x => x.PostedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<LedgerEntryEntity>> ListLedgerEntries(CancellationToken ct) =>
        await db.LedgerEntries.AsNoTracking().ToListAsync(ct);

    public Task<bool> JournalExists(string eventKey, CancellationToken ct) =>
        db.JournalEntries.AnyAsync(x => x.EventKey == eventKey, ct);

    public void Add(JournalEntryEntity journal) => db.JournalEntries.Add(journal);

    public async Task<IReadOnlyDictionary<string, decimal>> AccountBalances(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        await db.JournalLines.AsNoTracking()
            .Join(db.JournalEntries, line => line.JournalEntryId, entry => entry.Id, (line, entry) => new { line, entry })
            .Where(x => x.entry.PostedAt >= from && x.entry.PostedAt < to)
            .GroupBy(x => x.line.AccountCode)
            .Select(g => new { Account = g.Key, Balance = g.Sum(x => x.line.CreditAmount - x.line.DebitAmount) })
            .ToDictionaryAsync(x => x.Account, x => x.Balance, ct);

    public async Task<IReadOnlyList<SupplierInvoiceEntity>> ListInvoices(CancellationToken ct) =>
        await db.SupplierInvoices.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync(ct);

    public Task<SupplierInvoiceEntity?> FindInvoice(Guid id, CancellationToken ct) =>
        db.SupplierInvoices.SingleOrDefaultAsync(x => x.Id == id, ct);

    public void Add(SupplierInvoiceEntity invoice) => db.SupplierInvoices.Add(invoice);

    public Task<SupplierPaymentEntity?> FindPayment(string reference, CancellationToken ct) =>
        db.SupplierPayments.SingleOrDefaultAsync(x => x.PaymentReference == reference, ct);

    public void Add(SupplierPaymentEntity payment) => db.SupplierPayments.Add(payment);
}
