namespace SupermarketErp.Api.Features.Finance;

public sealed record CreateInvoiceRequest(
    Guid SupplierId,
    Guid? PurchaseOrderId,
    string InvoiceNumber,
    decimal Subtotal,
    decimal VatRate,
    DateOnly? IssueDate,
    DateOnly? DueDate);
public sealed record PayInvoiceRequest(
    string PaymentReference,
    decimal Amount,
    string PaymentMethod,
    DateOnly? PaidDate);
public sealed record CardSettlementRequest(
    string SettlementReference,
    decimal Amount,
    DateOnly? SettlementDate);
public sealed record LedgerEntryView(
    Guid Id,
    string EntryType,
    string Amount,
    string Description,
    Guid? ReferenceId,
    string CreatedAt);
public sealed record JournalLineView(
    Guid Id,
    string Account,
    string Debit,
    string Credit,
    string Description);
public sealed record JournalView(
    Guid Id,
    string EventKey,
    string Description,
    Guid? ReferenceId,
    string PostedAt,
    IReadOnlyList<JournalLineView> Lines);
public sealed record ProfitAndLossView(
    string From,
    string To,
    string Revenue,
    string OutputVat,
    string CostOfGoodsSold,
    string CashVariance,
    string GrossProfit,
    string NetProfit);
public sealed record InvoiceView(
    Guid Id,
    Guid SupplierId,
    Guid? PurchaseOrderId,
    string InvoiceNumber,
    string Subtotal,
    string TaxAmount,
    string Total,
    string PaidAmount,
    string Outstanding,
    string Status,
    string IssueDate,
    string? DueDate);
