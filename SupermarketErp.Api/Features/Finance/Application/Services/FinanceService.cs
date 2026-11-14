namespace SupermarketErp.Api.Features.Finance;

public sealed class FinanceService(IFinanceRepository repository) : IFinanceService
{
    public Task<IReadOnlyList<LedgerEntryView>> ListLedger(CancellationToken ct) =>
        repository.ListLedger(ct);

    public Task<IReadOnlyList<JournalView>> ListJournals(CancellationToken ct) =>
        repository.ListJournals(ct);

    public Task<ProfitAndLossView> ProfitAndLoss(
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct) =>
        repository.ProfitAndLoss(from, to, ct);

    public Task<IReadOnlyList<InvoiceView>> ListInvoices(CancellationToken ct) =>
        repository.ListInvoices(ct);

    public Task<InvoiceView> CreateInvoice(
        CreateInvoiceRequest request,
        CancellationToken ct) =>
        repository.CreateInvoice(request, ct);

    public Task<InvoiceView?> PayInvoice(
        Guid invoiceId,
        PayInvoiceRequest request,
        CancellationToken ct) =>
        repository.PayInvoice(invoiceId, request, ct);

    public Task SettleCard(CardSettlementRequest request, CancellationToken ct) =>
        repository.SettleCard(request, ct);
}
