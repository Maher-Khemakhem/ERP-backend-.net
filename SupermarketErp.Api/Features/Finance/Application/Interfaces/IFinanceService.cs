namespace SupermarketErp.Api.Features.Finance;

public interface IFinanceService
{
    Task<IReadOnlyList<LedgerEntryView>> ListLedger(CancellationToken ct);
    Task<IReadOnlyList<JournalView>> ListJournals(CancellationToken ct);
    Task<ProfitAndLossView> ProfitAndLoss(DateOnly? from, DateOnly? to, CancellationToken ct);
    Task<IReadOnlyList<InvoiceView>> ListInvoices(CancellationToken ct);
    Task<InvoiceView> CreateInvoice(CreateInvoiceRequest request, CancellationToken ct);
    Task<InvoiceView?> PayInvoice(Guid invoiceId, PayInvoiceRequest request, CancellationToken ct);
    Task SettleCard(CardSettlementRequest request, CancellationToken ct);
}
