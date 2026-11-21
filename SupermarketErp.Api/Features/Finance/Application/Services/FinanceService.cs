using System.Globalization;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Finance;

/// <summary>
/// Finance use cases: the books (journals and ledger), the profit-and-loss report, supplier invoices and payments,
/// and card settlement. It orchestrates; the entities enforce the accounting rules (balanced postings, invoice
/// VAT, payment limits). Postings are idempotent: the same event key is only booked once.
/// </summary>
public sealed class FinanceService(IFinanceRepository repository, IUnitOfWork unitOfWork) : IFinanceService
{
    // ---- the books -------------------------------------------------------------------------

    public async Task<IReadOnlyList<LedgerEntryView>> ListLedger(CancellationToken ct)
    {
        var journals = await repository.ListJournals(ct);
        var entries = journals.SelectMany(journal => journal.Lines.Select(line => new LedgerEntryView(
            line.Id, line.AccountCode, Money(line.DebitAmount == 0 ? line.CreditAmount : line.DebitAmount),
            $"{journal.Description} - {line.Description}", journal.ReferenceId, JavaInstantFormat.Format(journal.PostedAt)))).ToList();
        entries.AddRange((await repository.ListLedgerEntries(ct)).Select(x => new LedgerEntryView(
            x.Id, x.Type, Money(x.Amount), x.Description, x.ReferenceId, JavaInstantFormat.Format(x.RecordedAt))));
        return entries.OrderByDescending(x => x.CreatedAt).ToArray();
    }

    public async Task<IReadOnlyList<JournalView>> ListJournals(CancellationToken ct) =>
        (await repository.ListJournals(ct)).Select(journal => new JournalView(
            journal.Id, journal.EventKey, journal.Description, journal.ReferenceId, JavaInstantFormat.Format(journal.PostedAt),
            journal.Lines.Select(line => new JournalLineView(
                line.Id, line.AccountCode, Money(line.DebitAmount), Money(line.CreditAmount), line.Description)).ToArray())).ToArray();

    /// <summary>Revenue, VAT, cost of goods and cash variance from the journals of a period (this month by default).</summary>
    public async Task<ProfitAndLossView> ProfitAndLoss(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var start = from ?? new DateOnly(today.Year, today.Month, 1);
        var end = to ?? today;
        if (end < start) throw new DomainValidationException("to cannot be before from");

        var balances = await repository.AccountBalances(
            new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)),
            new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)), ct);

        var revenue = balances.GetValueOrDefault(AccountCodes.SalesRevenue);
        var outputVat = balances.GetValueOrDefault(AccountCodes.OutputVat);
        var costOfGoodsSold = -balances.GetValueOrDefault(AccountCodes.CostOfGoodsSold);
        var cashVariance = balances.GetValueOrDefault(AccountCodes.CashVariance);
        var grossProfit = revenue - costOfGoodsSold;
        return new ProfitAndLossView(start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"), Money(revenue), Money(outputVat),
            Money(costOfGoodsSold), Money(cashVariance), Money(grossProfit), Money(grossProfit + cashVariance));
    }

    // ---- supplier invoices and payments ----------------------------------------------------

    public async Task<IReadOnlyList<InvoiceView>> ListInvoices(CancellationToken ct) =>
        (await repository.ListInvoices(ct)).Select(Invoice).ToArray();

    public async Task<InvoiceView> CreateInvoice(CreateInvoiceRequest request, CancellationToken ct)
    {
        var invoice = SupplierInvoiceEntity.Create(request.SupplierId, request.PurchaseOrderId, request.InvoiceNumber, request.Subtotal,
            request.VatRate, request.IssueDate, request.DueDate, DateOnly.FromDateTime(DateTime.Today), DateTimeOffset.UtcNow);
        await unitOfWork.InTransaction(async () =>
        {
            repository.Add(invoice);
            await Post($"supplier-invoice:{invoice.Id}", $"Supplier invoice {invoice.InvoiceNumber}", invoice.Id, invoice.PostingLines(), ct);
        }, ct);
        return Invoice(invoice);
    }

    /// <summary>
    /// Pays (part of) an invoice. The payment reference makes it idempotent: repeating a request returns the invoice
    /// unchanged. Returns null when the invoice does not exist.
    /// </summary>
    public async Task<InvoiceView?> PayInvoice(Guid invoiceId, PayInvoiceRequest request, CancellationToken ct)
    {
        SupplierPaymentEntity.ValidateRequest(request.PaymentReference, request.Amount, request.PaymentMethod);
        var reference = request.PaymentReference.Trim();
        return await unitOfWork.InTransaction(async () =>
        {
            var duplicate = await repository.FindPayment(reference, ct);
            if (duplicate is not null)
            {
                if (duplicate.InvoiceId != invoiceId) throw new DomainValidationException("Payment reference already belongs to another invoice.");
                return await repository.FindInvoice(invoiceId, ct) is { } existing ? Invoice(existing) : null;
            }

            var invoice = await repository.FindInvoice(invoiceId, ct);
            if (invoice is null) return null;

            var amount = SupplierInvoiceEntity.Money(request.Amount);
            invoice.ApplyPayment(amount);
            var payment = SupplierPaymentEntity.Create(reference, invoiceId, amount, request.PaymentMethod,
                request.PaidDate ?? DateOnly.FromDateTime(DateTime.Today));
            repository.Add(payment);
            await Post($"supplier-payment:{reference}", $"Payment for supplier invoice {invoice.InvoiceNumber}", invoiceId, payment.PostingLines(), ct);
            return Invoice(invoice);
        }, ct);
    }

    public Task SettleCard(CardSettlementRequest request, CancellationToken ct)
    {
        CardSettlement.Validate(request.SettlementReference, request.Amount);
        var reference = request.SettlementReference.Trim();
        var amount = SupplierInvoiceEntity.Money(request.Amount);
        return unitOfWork.InTransaction(
            () => Post($"card-settlement:{reference}", $"Card settlement {reference}", null, CardSettlement.PostingLines(amount), ct), ct);
    }

    // ---- helpers ---------------------------------------------------------------------------

    /// <summary>Books a journal entry unless one with the same event key already exists.</summary>
    private async Task Post(string eventKey, string description, Guid? reference, IReadOnlyList<JournalLineInput> lines, CancellationToken ct)
    {
        var entry = JournalEntryEntity.Post(eventKey, description, reference, lines, DateTimeOffset.UtcNow);
        if (await repository.JournalExists(eventKey, ct)) return;
        repository.Add(entry);
    }

    private static InvoiceView Invoice(SupplierInvoiceEntity x) => new(
        x.Id, x.SupplierId, x.PurchaseOrderId, x.InvoiceNumber, Money(x.Subtotal), Money(x.TaxAmount), Money(x.TotalAmount),
        Money(x.PaidAmount), Money(x.Outstanding), x.Status, x.IssueDate.ToString("yyyy-MM-dd"), x.DueDate?.ToString("yyyy-MM-dd"));

    private static string Money(decimal amount) => $"{amount.ToString("0.000", CultureInfo.InvariantCulture)} TND";
}
