using System.Security.Cryptography;

namespace SupermarketErp.Api.Features.Ecommerce;

public static class OnlineOrderStatus
{
    public const string PendingPayment = "PENDING_PAYMENT";
    public const string Confirmed = "CONFIRMED";
    public const string Preparing = "PREPARING";
    public const string Ready = "READY";
    public const string Completed = "COMPLETED";
    public const string Cancelled = "CANCELLED";
    public const string Expired = "EXPIRED";
}

/// <summary>A customer's shopping cart: one per customer.</summary>
public sealed class CartEntity
{
    private CartEntity() { } // EF Core

    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public long Version { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static CartEntity Create(Guid customerId, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), CustomerId = customerId, CreatedAt = now, UpdatedAt = now };

    public void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }
}

public sealed class CartItemEntity
{
    private CartItemEntity() { } // EF Core

    public Guid Id { get; private set; }
    public Guid CartId { get; private set; }
    public Guid ProductId { get; private set; }
    public decimal Quantity { get; private set; }
    public string UnitType { get; private set; } = "";

    public static CartItemEntity Create(Guid cartId, Guid productId, decimal quantity, string unitType) =>
        new() { Id = Guid.NewGuid(), CartId = cartId, ProductId = productId, Quantity = quantity, UnitType = unitType };

    /// <summary>Adds to the quantity already in the cart.</summary>
    public void Increase(decimal quantity, string unitType)
    {
        Quantity += quantity;
        UnitType = unitType;
    }

    public void SetQuantity(decimal quantity, string unitType)
    {
        Quantity = quantity;
        UnitType = unitType;
    }

    public static void ValidateQuantity(decimal value, string unitType)
    {
        if (value <= 0) throw new DomainValidationException("Quantity must be positive.");
        var unit = unitType.Trim().ToUpperInvariant();
        if (unit is not ("EACH" or "WEIGHT")) throw new DomainValidationException("Unit type must be EACH or WEIGHT.");
        if (unit == "EACH" && value != decimal.Truncate(value)) throw new DomainValidationException("EACH quantity must be a whole number.");
        if (unit == "WEIGHT" && decimal.Round(value, 3) != value) throw new DomainValidationException("WEIGHT quantity supports three decimal places.");
    }
}

/// <summary>
/// An online order. Checkout places it (stock is reserved) as PENDING_PAYMENT; paying confirms it (stock is sold);
/// cancelling or letting it expire gives the stock back. Staff then prepare it and hand it over with a pickup code.
/// </summary>
public sealed class OnlineOrderEntity
{
    private OnlineOrderEntity() { } // EF Core

    public Guid Id { get; private set; }
    public string OrderNumber { get; private set; } = "";
    public Guid CustomerId { get; private set; }
    public string Status { get; private set; } = OnlineOrderStatus.PendingPayment;
    public decimal NetAmount { get; private set; }
    public decimal VatAmount { get; private set; }
    public decimal TotalAmount { get; private set; }
    public string? PaymentReference { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }
    public long Version { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public DateTimeOffset? ExpiredAt { get; private set; }
    public string? PickupCode { get; private set; }
    public DateTimeOffset? PickedUpAt { get; private set; }
    public string? PickedUpBy { get; private set; }
    public List<OnlineOrderLineEntity> Lines { get; private set; } = [];

    public bool IsConfirmed => Status == OnlineOrderStatus.Confirmed;
    public bool IsClosedWithoutPayment => Status is OnlineOrderStatus.Cancelled or OnlineOrderStatus.Expired;

    /// <summary>Places an order awaiting payment. Add its lines with <see cref="AddLine"/>.</summary>
    public static OnlineOrderEntity Place(Guid customerId, decimal net, decimal vat, DateTimeOffset now, int paymentWindowMinutes) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrderNumber = "ONL-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
            CustomerId = customerId,
            Status = OnlineOrderStatus.PendingPayment,
            NetAmount = net,
            VatAmount = vat,
            TotalAmount = net + vat,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(paymentWindowMinutes)
        };

    public void AddLine(Guid productId, string productName, decimal quantity, string unitType, decimal unitPrice, decimal lineTotal, decimal vatAmount) =>
        Lines.Add(OnlineOrderLineEntity.Create(Id, productId, productName, quantity, unitType, unitPrice, lineTotal, vatAmount));

    public bool PaymentWindowIsOver(DateTimeOffset now) => ExpiresAt <= now;

    /// <summary>A card checkout may keep the order waiting for a while longer.</summary>
    public void ExtendPaymentWindow(DateTimeOffset deadline)
    {
        if (ExpiresAt < deadline) ExpiresAt = deadline;
        Version++;
    }

    public void RequirePendingPayment()
    {
        if (Status != OnlineOrderStatus.PendingPayment) throw new ConflictException("Only pending orders can be paid.");
    }

    public void Confirm(string paymentReference, DateTimeOffset now)
    {
        RequirePendingPayment();
        Status = OnlineOrderStatus.Confirmed;
        PaymentReference = paymentReference;
        ConfirmedAt = now;
        Version++;
    }

    public void Expire(DateTimeOffset now)
    {
        Status = OnlineOrderStatus.Expired;
        ExpiredAt = now;
        Version++;
    }

    public void RequireUnpaid()
    {
        if (Status != OnlineOrderStatus.PendingPayment) throw new ConflictException("Only unpaid orders can be cancelled.");
    }

    public void Cancel(DateTimeOffset now)
    {
        RequireUnpaid();
        Status = OnlineOrderStatus.Cancelled;
        CancelledAt = now;
        Version++;
    }

    /// <summary>Staff can only move an order to PREPARING or READY.</summary>
    public static string ParseStaffStatus(string? raw)
    {
        var status = raw?.Trim().ToUpperInvariant();
        return status is OnlineOrderStatus.Preparing or OnlineOrderStatus.Ready
            ? status : throw new DomainValidationException("Staff can advance orders to PREPARING or READY.");
    }

    /// <summary>CONFIRMED -> PREPARING -> READY. Becoming ready creates the code the customer shows at pickup.</summary>
    public void AdvanceTo(string next)
    {
        var allowed = (Status == OnlineOrderStatus.Confirmed && next == OnlineOrderStatus.Preparing)
            || (Status == OnlineOrderStatus.Preparing && next == OnlineOrderStatus.Ready);
        if (!allowed) throw new ConflictException($"Online order cannot move from {Status} to {next}.");
        Status = next;
        PickupCode = next == OnlineOrderStatus.Ready ? Convert.ToHexString(RandomNumberGenerator.GetBytes(6)) : null;
        Version++;
    }

    public void CompletePickup(string? code, string staff, DateTimeOffset now)
    {
        if (Status != OnlineOrderStatus.Ready || string.IsNullOrWhiteSpace(code))
            throw new ConflictException("Only ready orders can be collected with a pickup code.");
        var normalized = code.Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(normalized), System.Text.Encoding.ASCII.GetBytes(PickupCode ?? "")))
            throw new DomainValidationException("Invalid pickup code.");
        Status = OnlineOrderStatus.Completed;
        PickupCode = null;
        PickedUpAt = now;
        PickedUpBy = staff;
        Version++;
    }
}

public sealed class OnlineOrderLineEntity
{
    private OnlineOrderLineEntity() { } // EF Core

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public string ProductName { get; private set; } = "";
    public decimal Quantity { get; private set; }
    public string UnitType { get; private set; } = "";
    public decimal UnitPrice { get; private set; }
    public decimal LineTotal { get; private set; }
    public decimal VatAmount { get; private set; }

    internal static OnlineOrderLineEntity Create(Guid orderId, Guid productId, string productName, decimal quantity, string unitType,
        decimal unitPrice, decimal lineTotal, decimal vatAmount) =>
        new()
        {
            Id = Guid.NewGuid(), OrderId = orderId, ProductId = productId, ProductName = productName, Quantity = quantity,
            UnitType = unitType, UnitPrice = unitPrice, LineTotal = lineTotal, VatAmount = vatAmount
        };
}
