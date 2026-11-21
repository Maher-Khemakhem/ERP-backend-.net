using System.Globalization;
using SupermarketErp.Api.Features.Pricing;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Pos;

/// <summary>Maps POS entities to the API's response shapes. Text is always formatted with the invariant culture.</summary>
internal static class PosViews
{
    public static TillView Till(TillEntity till, CashSessionEntity? latestSession) => new(
        till.Id, till.TillNumber, till.Status, latestSession?.CashierName,
        Money(latestSession is { IsOpen: true } ? latestSession.ExpectedCash : latestSession?.CountedCash ?? 0m),
        latestSession is null ? null : JavaInstantFormat.Format(latestSession.OpenedAt),
        latestSession?.ClosedAt is null ? null : JavaInstantFormat.Format(latestSession.ClosedAt.Value));

    public static SessionView Session(CashSessionEntity session) =>
        new(session.Id, session.TillId, session.Status, Money(session.ExpectedCash));

    public static TransactionView Transaction(PosTransactionEntity ticket) => new(
        ticket.Id, ticket.CashSessionId, ticket.Status, Money(ticket.Total), Money(ticket.NetTotal), Money(ticket.VatTotal),
        ticket.PaymentMethod, JavaInstantFormat.Format(ticket.StartedAt),
        ticket.CompletedAt is null ? null : JavaInstantFormat.Format(ticket.CompletedAt.Value),
        ticket.Lines.OrderBy(x => x.Id).Select(Line).ToList());

    public static AddLineResponse AddedLine(CalculatedPrice price, TransactionLineEntity line) =>
        new(Money(price.Gross), Money(price.Net), Money(price.Vat), line.Id.ToString());

    private static PosLineView Line(TransactionLineEntity line) => new(
        line.Id, line.ProductId, Quantity(line), Money(line.LineTotal), Money(line.NetTotal), Money(line.VatAmount));

    private static string Quantity(TransactionLineEntity line) => line.UnitType == 0
        ? $"{line.Quantity.ToString("0", CultureInfo.InvariantCulture)} unit(s)"
        : $"{line.Quantity.ToString("0.000", CultureInfo.InvariantCulture)} kg";

    public static string Money(decimal amount) => $"{amount.ToString("0.000", CultureInfo.InvariantCulture)} TND";
}
