namespace SupermarketErp.Api.Features.Ecommerce;

public sealed record StorefrontProductView(Guid Id, string Sku, string Name, Guid? FamilyId,
    string? FamilyName, string? FamilyImageUrl, string? ImageUrl, string UnitType, string UnitPrice,
    decimal VatRate, decimal AvailableQuantity, bool InStock);
public sealed record QuantityRequest(Guid ProductId, decimal QuantityValue, string UnitType);
public sealed record CartItemView(Guid ProductId, string ProductName, string Quantity, string UnitType,
    string UnitPrice, string LineTotal, decimal AvailableQuantity, bool InStock);
public sealed record CartView(IReadOnlyList<CartItemView> Items, string Subtotal, string VatAmount, string TotalAmount);
public sealed record OrderLineView(Guid ProductId, string ProductName, string Quantity, string UnitType,
    string UnitPrice, string LineTotal, string VatAmount);
public sealed record OnlineOrderView(Guid Id, string OrderNumber, string Status, string NetAmount,
    string VatAmount, string TotalAmount, string? PaymentReference, string CreatedAt, string? ConfirmedAt,
    string ExpiresAt, string? CancelledAt, string? ExpiredAt, string? StripeStatus, string? PickupCode,
    IReadOnlyList<OrderLineView> Lines);
public sealed record StaffOnlineOrderView(Guid Id, string OrderNumber, Guid CustomerId, string CustomerUsername,
    string CustomerName, string Status, string NetAmount, string VatAmount, string TotalAmount,
    string? PaymentReference, string CreatedAt, string? ConfirmedAt, string ExpiresAt, string? CancelledAt,
    string? ExpiredAt, string? PickedUpAt, string? PickedUpBy, IReadOnlyList<OrderLineView> Lines);
public sealed record FulfillmentStatusRequest(string Status);
public sealed record PickupRequest(string Code);
public sealed record StripeConfigurationView(bool Enabled, string Currency, decimal Rate,
    long MinimumAmountCents, long MaximumAmountCents);
public sealed record StripeQuote(string Currency, decimal TndAmount, long AmountCents, decimal Rate);
public sealed record StripeCheckoutResult(string Url, StripeQuote Quote);
