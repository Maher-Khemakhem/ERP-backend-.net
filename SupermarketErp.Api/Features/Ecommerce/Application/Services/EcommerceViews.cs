using System.Globalization;
using SupermarketErp.Api.Features.Auth;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Ecommerce;

/// <summary>Maps storefront entities to the API's response shapes. Text is always formatted with the invariant culture.</summary>
internal static class EcommerceViews
{
    public static OnlineOrderView Order(OnlineOrderEntity order, string? stripeStatus) => new(
        order.Id, order.OrderNumber, order.Status, Money(order.NetAmount), Money(order.VatAmount), Money(order.TotalAmount),
        order.PaymentReference, JavaInstantFormat.Format(order.CreatedAt), Instant(order.ConfirmedAt),
        JavaInstantFormat.Format(order.ExpiresAt), Instant(order.CancelledAt), Instant(order.ExpiredAt), stripeStatus,
        order.PickupCode is null ? null : $"{order.PickupCode[..6]}-{order.PickupCode[6..]}",
        order.Lines.OrderBy(x => x.Id).Select(x => new OrderLineView(
            x.ProductId, x.ProductName, Quantity(x.Quantity, x.UnitType), x.UnitType,
            Money(x.UnitPrice), Money(x.LineTotal), Money(x.VatAmount))).ToList());

    /// <summary>The staff view adds the customer. A missing customer shows as "Unknown".</summary>
    public static StaffOnlineOrderView StaffOrder(OnlineOrderEntity order, string? stripeStatus, UserView? customer)
    {
        var view = Order(order, stripeStatus);
        return new StaffOnlineOrderView(
            view.Id, view.OrderNumber,
            customer?.Id ?? Guid.Empty, customer?.Username ?? "Unknown", customer?.DisplayName ?? "Unknown customer",
            view.Status, view.NetAmount, view.VatAmount, view.TotalAmount, view.PaymentReference, view.CreatedAt,
            view.ConfirmedAt, view.ExpiresAt, view.CancelledAt, view.ExpiredAt,
            customer is null ? null : Instant(order.PickedUpAt), customer is null ? null : order.PickedUpBy, view.Lines);
    }

    public static CartView Cart(IReadOnlyList<CartRow> rows)
    {
        decimal subtotal = 0, vat = 0;
        var items = rows.Select(row =>
        {
            var lineTotal = Round(row.Price * row.Quantity);
            subtotal += lineTotal;
            vat += Round(lineTotal * row.VatRate / 100m);
            return new CartItemView(row.ProductId, row.Name, Quantity(row.Quantity, row.Unit), row.Unit, Money(row.Price),
                Money(lineTotal), row.Available, row.Available > 0);
        }).ToList();
        subtotal = Round(subtotal);
        vat = Round(vat);
        return new CartView(items, Money(subtotal), Money(vat), Money(subtotal + vat));
    }

    public static decimal Round(decimal amount) => decimal.Round(amount, 3, MidpointRounding.AwayFromZero);
    public static string Money(decimal amount) => $"{amount.ToString("0.000", CultureInfo.InvariantCulture)} TND";

    private static string Quantity(decimal quantity, string unit) => unit == "EACH"
        ? $"{quantity.ToString("0", CultureInfo.InvariantCulture)} unit(s)"
        : $"{quantity.ToString("0.000", CultureInfo.InvariantCulture)} kg";

    private static string? Instant(DateTimeOffset? value) => value is null ? null : JavaInstantFormat.Format(value.Value);
}

/// <summary>A cart line joined with product, price and stock information from the other modules.</summary>
internal sealed record CartRow(Guid ProductId, string Name, decimal Quantity, string Unit, decimal Price, decimal VatRate,
    decimal Available, bool Visible, bool HasPrice);
