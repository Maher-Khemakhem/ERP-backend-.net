using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Features.Auth;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Finance;

public sealed class FinanceRepository(BusinessDbContext db) : IFinanceRepository
{
    public async Task<IReadOnlyList<LedgerEntryView>> ListLedger(CancellationToken ct)
    {
        var journals = await db.JournalEntries.AsNoTracking().Include(x => x.Lines).ToListAsync(ct);
        var result = journals.SelectMany(j => j.Lines.Select(l => new LedgerEntryView(l.Id, l.AccountCode, FormatMoney(l.DebitAmount == 0 ? l.CreditAmount : l.DebitAmount), $"{j.Description} - {l.Description}", j.ReferenceId, JavaInstantFormat.Format(j.PostedAt)))).ToList();
        result.AddRange(await db.LedgerEntries.AsNoTracking().Select(x => new LedgerEntryView(x.Id, x.Type, FormatMoney(x.Amount), x.Description, x.ReferenceId, JavaInstantFormat.Format(x.RecordedAt))).ToListAsync(ct));
        return result.OrderByDescending(x => x.CreatedAt).ToArray();
    }
    public async Task<IReadOnlyList<JournalView>> ListJournals(CancellationToken ct) =>
        (await db.JournalEntries.AsNoTracking().Include(x => x.Lines).OrderByDescending(x => x.PostedAt).ToListAsync(ct))
        .Select(x => new JournalView(x.Id, x.EventKey, x.Description, x.ReferenceId, JavaInstantFormat.Format(x.PostedAt),
            x.Lines.Select(l => new JournalLineView(l.Id, l.AccountCode, FormatMoney(l.DebitAmount), FormatMoney(l.CreditAmount), l.Description)).ToArray())).ToArray();
    public async Task<ProfitAndLossView> ProfitAndLoss(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Now); var start = from ?? new DateOnly(today.Year, today.Month, 1); var end = to ?? today;
        if (end < start) throw new ArgumentException("to cannot be before from");
        var balances = await db.JournalLines.AsNoTracking().Where(x => x.JournalEntryId != Guid.Empty)
            .Join(db.JournalEntries, l => l.JournalEntryId, j => j.Id, (l, j) => new { l, j })
            .Where(x => x.j.PostedAt >= new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)) && x.j.PostedAt < new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)))
            .GroupBy(x => x.l.AccountCode).Select(x => new { x.Key, Balance = x.Sum(v => v.l.CreditAmount - v.l.DebitAmount) }).ToDictionaryAsync(x => x.Key, x => x.Balance, ct);
        var revenue = balances.GetValueOrDefault("SALES_REVENUE"); var vat = balances.GetValueOrDefault("OUTPUT_VAT"); var cogs = -balances.GetValueOrDefault("COST_OF_GOODS_SOLD"); var variance = balances.GetValueOrDefault("CASH_VARIANCE"); var gross = revenue - cogs;
        return new ProfitAndLossView(start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"), FormatMoney(revenue), FormatMoney(vat), FormatMoney(cogs), FormatMoney(variance), FormatMoney(gross), FormatMoney(gross + variance));
    }
    public async Task<IReadOnlyList<InvoiceView>> ListInvoices(CancellationToken ct) => (await db.SupplierInvoices.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync(ct)).Select(ToView).ToArray();
    public async Task<InvoiceView> CreateInvoice(CreateInvoiceRequest request, CancellationToken ct)
    {
        ValidateInvoice(request); var subtotal = Money(request.Subtotal); var tax = Money(subtotal * request.VatRate / 100m); var total = subtotal + tax; var issue = request.IssueDate ?? DateOnly.FromDateTime(DateTime.Today);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var invoice = new SupplierInvoiceEntity { Id = Guid.NewGuid(), SupplierId = request.SupplierId, PurchaseOrderId = request.PurchaseOrderId, InvoiceNumber = request.InvoiceNumber.Trim(), Subtotal = subtotal, TaxAmount = tax, TotalAmount = total, Status = "OPEN", IssueDate = issue, DueDate = request.DueDate, CreatedAt = DateTimeOffset.UtcNow };
        db.SupplierInvoices.Add(invoice); var lines = new List<Line> { new("GOODS_RECEIVED_NOT_INVOICED", subtotal, 0, "Invoice net amount") }; if (tax != 0) lines.Add(new("INPUT_VAT", tax, 0, "Input VAT")); lines.Add(new("ACCOUNTS_PAYABLE", 0, total, "Supplier payable"));
        await PostJournal($"supplier-invoice:{invoice.Id}", $"Supplier invoice {invoice.InvoiceNumber}", invoice.Id, lines, ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return ToView(invoice);
    }
    public async Task<InvoiceView?> PayInvoice(Guid invoiceId, PayInvoiceRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PaymentReference) || request.Amount <= 0 || string.IsNullOrWhiteSpace(request.PaymentMethod)) throw new ArgumentException("Payment reference, positive amount and payment method are required.");
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var reference = request.PaymentReference.Trim(); var duplicate = await db.SupplierPayments.SingleOrDefaultAsync(x => x.PaymentReference == reference, ct);
        if (duplicate is not null) { if (duplicate.InvoiceId != invoiceId) throw new ArgumentException("Payment reference already belongs to another invoice."); await tx.CommitAsync(ct); var existingInvoice = await db.SupplierInvoices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == invoiceId, ct); return existingInvoice is null ? null : ToView(existingInvoice); }
        var invoice = await db.SupplierInvoices.SingleOrDefaultAsync(x => x.Id == invoiceId, ct); if (invoice is null) return null;
        if (invoice.Status is "PAID" or "CANCELLED") throw new InvalidOperationException("Invoice cannot accept more payments.");
        var amount = Money(request.Amount); if (amount > invoice.TotalAmount - invoice.PaidAmount) throw new ArgumentException("Payment exceeds outstanding invoice balance.");
        invoice.PaidAmount += amount; invoice.Status = invoice.PaidAmount == invoice.TotalAmount ? "PAID" : "PARTIALLY_PAID"; invoice.Version++;
        db.SupplierPayments.Add(new SupplierPaymentEntity { Id = Guid.NewGuid(), PaymentReference = reference, InvoiceId = invoiceId, Amount = amount, PaymentMethod = request.PaymentMethod.Trim().ToUpperInvariant(), PaidDate = request.PaidDate ?? DateOnly.FromDateTime(DateTime.Today) });
        var source = request.PaymentMethod.Trim().Equals("CASH", StringComparison.OrdinalIgnoreCase) ? "CASH_ON_HAND" : "BANK";
        await PostJournal($"supplier-payment:{reference}", $"Payment for supplier invoice {invoice.InvoiceNumber}", invoiceId, [new("ACCOUNTS_PAYABLE", amount, 0, "Reduce supplier payable"), new(source, 0, amount, "Supplier payment")], ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return ToView(invoice);
    }
    public async Task SettleCard(CardSettlementRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SettlementReference) || request.Amount <= 0) throw new ArgumentException("Settlement reference and positive amount are required.");
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        await PostJournal($"card-settlement:{request.SettlementReference.Trim()}", $"Card settlement {request.SettlementReference.Trim()}", null, [new("BANK", Money(request.Amount), 0, "Card settlement received"), new("CARD_CLEARING", 0, Money(request.Amount), "Clear card receivable")], ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    private async Task PostJournal(string key, string description, Guid? reference, IReadOnlyList<Line> lines, CancellationToken ct)
    {
        if (lines.Sum(x => x.Debit) != lines.Sum(x => x.Credit)) throw new InvalidOperationException($"Journal entry {key} is unbalanced.");
        if (await db.JournalEntries.AnyAsync(x => x.EventKey == key, ct)) return;
        db.JournalEntries.Add(new JournalEntryEntity { Id = Guid.NewGuid(), EventKey = key, Description = description, ReferenceId = reference, PostedAt = DateTimeOffset.UtcNow, Lines = lines.Select(x => new JournalLineEntity { Id = Guid.NewGuid(), AccountCode = x.Account, DebitAmount = x.Debit, CreditAmount = x.Credit, Description = x.Description }).ToList() });
    }
    private static InvoiceView ToView(SupplierInvoiceEntity x) => new(x.Id, x.SupplierId, x.PurchaseOrderId, x.InvoiceNumber, FormatMoney(x.Subtotal), FormatMoney(x.TaxAmount), FormatMoney(x.TotalAmount), FormatMoney(x.PaidAmount), FormatMoney(x.TotalAmount - x.PaidAmount), x.Status, x.IssueDate.ToString("yyyy-MM-dd"), x.DueDate?.ToString("yyyy-MM-dd"));
    private static void ValidateInvoice(CreateInvoiceRequest r) { if (r.SupplierId == Guid.Empty || string.IsNullOrWhiteSpace(r.InvoiceNumber) || r.Subtotal <= 0 || r.VatRate is < 0 or > 100) throw new ArgumentException("Invalid supplier invoice."); if (r.DueDate is not null && r.DueDate < (r.IssueDate ?? DateOnly.FromDateTime(DateTime.Today))) throw new ArgumentException("Due date cannot be before the issue date."); }
    private static decimal Money(decimal x) => decimal.Round(x, 3, MidpointRounding.AwayFromZero);
    private static string FormatMoney(decimal x) => $"{x.ToString("0.000", CultureInfo.InvariantCulture)} TND";
    private sealed record Line(string Account, decimal Debit, decimal Credit, string Description);
}
