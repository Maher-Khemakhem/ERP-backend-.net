using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Finance;

/// <summary>Books a point-of-sale sale. Idempotent through the journal's event key.</summary>
public sealed class SaleCompletedHandler(JournalPoster poster) : OutboxHandler<SaleCompletedEvent>("SaleCompleted")
{
    protected override Task Handle(SaleCompletedEvent e, CancellationToken ct) =>
        poster.PostOnce($"sale:{e.TransactionId}", $"POS sale {e.TransactionId}", e.TransactionId,
            AutomaticPostings.Sale(e.PaymentMethod, e.NetAmount, e.VatAmount, e.GrossAmount, e.CostOfGoodsSold), ct);
}

/// <summary>Books a confirmed (paid) online order the same way as a till sale, under its own key.</summary>
public sealed class OnlineSaleConfirmedHandler(JournalPoster poster) : OutboxHandler<SaleCompletedEvent>("OnlineSaleConfirmed")
{
    protected override Task Handle(SaleCompletedEvent e, CancellationToken ct) =>
        poster.PostOnce($"online-sale:{e.TransactionId}", $"Online sale {e.TransactionId}", e.TransactionId,
            AutomaticPostings.Sale(e.PaymentMethod, e.NetAmount, e.VatAmount, e.GrossAmount, e.CostOfGoodsSold), ct);
}

public sealed class CashSessionClosedHandler(JournalPoster poster) : OutboxHandler<CashSessionClosedEvent>("CashSessionClosed")
{
    protected override Task Handle(CashSessionClosedEvent e, CancellationToken ct) =>
        poster.PostOnce($"cash-close:{e.CashSessionId}", $"Cash session close {e.CashSessionId}", e.CashSessionId,
            AutomaticPostings.CashClose(e.ExpectedCash, e.CountedCash), ct);
}

public sealed class GoodsReceivedHandler(JournalPoster poster) : OutboxHandler<GoodsReceivedEvent>("GoodsReceived")
{
    protected override Task Handle(GoodsReceivedEvent e, CancellationToken ct) =>
        poster.PostOnce($"goods-received:{e.PurchaseOrderId}", $"Goods received for purchase order {e.PurchaseOrderId}",
            e.PurchaseOrderId, AutomaticPostings.GoodsReceived(e.TotalCost), ct);
}
