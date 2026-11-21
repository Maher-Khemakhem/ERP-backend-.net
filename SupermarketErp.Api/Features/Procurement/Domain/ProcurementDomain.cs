namespace SupermarketErp.Api.Features.Procurement;

public static class PurchaseOrderStatus
{
    public const string Draft = "DRAFT";
    public const string Placed = "PLACED";
    public const string Received = "RECEIVED";
    public const string Cancelled = "CANCELLED";
}

public sealed class SupplierEntity
{
    private SupplierEntity() { } // EF Core

    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public string? ContactEmail { get; private set; }
    public string? ContactPhone { get; private set; }
    public string? Address { get; private set; }

    public static SupplierEntity Create(string? name, string? contactEmail, string? phone, string? address)
    {
        var supplier = new SupplierEntity { Id = Guid.NewGuid() };
        supplier.Update(name, contactEmail, phone, address);
        return supplier;
    }

    public void Update(string? name, string? contactEmail, string? phone, string? address)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainValidationException("Supplier name is required.");
        if (name.Trim().Length > 255) throw new DomainValidationException("Supplier name is too long.");
        if (address?.Length > 500) throw new DomainValidationException("Address cannot exceed 500 characters.");
        Name = name.Trim();
        ContactEmail = contactEmail;
        ContactPhone = phone;
        Address = address;
    }
}

/// <summary>What the warehouse reports for one order line when goods arrive.</summary>
public sealed record ReceivedLine(Guid PurchaseOrderLineId, decimal Quantity, string UnitType);

/// <summary>An order to a supplier: draft -> placed -> received, or cancelled while it is draft or placed.</summary>
public sealed class PurchaseOrderEntity
{
    private PurchaseOrderEntity() { } // EF Core

    public Guid Id { get; private set; }
    public Guid SupplierId { get; private set; }
    public string Status { get; private set; } = PurchaseOrderStatus.Draft;
    public DateTimeOffset PlacedAt { get; private set; }
    public List<PurchaseOrderLineEntity> Lines { get; private set; } = [];

    public decimal TotalCost => Lines.Sum(x => x.Quantity * x.UnitCost);

    public static PurchaseOrderEntity Draft(Guid supplierId, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), SupplierId = supplierId, Status = PurchaseOrderStatus.Draft, PlacedAt = now };

    public void AddLine(Guid productId, decimal quantity, string unitType, decimal unitCost)
    {
        PurchaseOrderLineEntity.ValidateQuantity(quantity, unitType);
        if (unitCost < 0) throw new DomainValidationException("Unit cost cannot be negative.");
        Lines.Add(PurchaseOrderLineEntity.Create(Id, productId, quantity, unitType, unitCost));
    }

    public void Place()
    {
        if (Status != PurchaseOrderStatus.Draft)
            throw new ConflictException($"Purchase order {Id}: expected DRAFT, current status {Status}");
        if (Lines.Count == 0) throw new ConflictException("Impossible d'envoyer une commande sans lignes");
        Status = PurchaseOrderStatus.Placed;
    }

    public void Cancel()
    {
        if (Status is not (PurchaseOrderStatus.Draft or PurchaseOrderStatus.Placed))
            throw new ConflictException($"Commande {Id} : seule une commande DRAFT ou PLACED peut être annulée");
        Status = PurchaseOrderStatus.Cancelled;
    }

    /// <summary>A receipt names every line of the order exactly once.</summary>
    public static void RequireWellFormedReceipt(IReadOnlyList<ReceivedLine>? received)
    {
        if (received is null || received.Count == 0 || received.Select(x => x.PurchaseOrderLineId).Distinct().Count() != received.Count)
            throw new DomainValidationException("A receipt must contain every order line exactly once.");
    }

    /// <summary>
    /// Checks a receipt against the order and returns the order line for each received line, in the same order.
    /// The order must be placed, every line must be received exactly as ordered (quantity and unit).
    /// </summary>
    public IReadOnlyList<PurchaseOrderLineEntity> MatchReceipt(IReadOnlyList<ReceivedLine> received)
    {
        if (Status != PurchaseOrderStatus.Placed) throw new ConflictException("Only a placed purchase order can be received.");
        if (Lines.Count != received.Count || received.Any(x => Lines.All(l => l.Id != x.PurchaseOrderLineId)))
            throw new DomainValidationException("A receipt must include every order line exactly once.");

        var matched = new List<PurchaseOrderLineEntity>();
        foreach (var receipt in received)
        {
            PurchaseOrderLineEntity.ValidateQuantity(receipt.Quantity, receipt.UnitType);
            var line = Lines.Single(x => x.Id == receipt.PurchaseOrderLineId);
            if (line.UnitType != PurchaseOrderLineEntity.UnitCode(receipt.UnitType) || line.Quantity != receipt.Quantity)
                throw new DomainValidationException("Received quantity and unit must match the purchase order.");
            matched.Add(line);
        }
        return matched;
    }

    public void MarkReceived() => Status = PurchaseOrderStatus.Received;
}

public sealed class PurchaseOrderLineEntity
{
    private PurchaseOrderLineEntity() { } // EF Core

    public Guid Id { get; private set; }
    public Guid PurchaseOrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public decimal Quantity { get; private set; }
    public short UnitType { get; private set; }
    public decimal UnitCost { get; private set; }

    public string UnitName => UnitType == 0 ? "EACH" : "WEIGHT";

    internal static PurchaseOrderLineEntity Create(Guid orderId, Guid productId, decimal quantity, string unitType, decimal unitCost) =>
        new()
        {
            Id = Guid.NewGuid(), PurchaseOrderId = orderId, ProductId = productId, Quantity = quantity,
            UnitType = UnitCode(unitType), UnitCost = unitCost
        };

    public static short UnitCode(string unitType) =>
        unitType.Equals("EACH", StringComparison.OrdinalIgnoreCase) ? (short)0 : (short)1;

    public static void ValidateQuantity(decimal value, string unitType)
    {
        if (value <= 0) throw new DomainValidationException("Quantity must be positive.");
        if (unitType is not ("EACH" or "each" or "WEIGHT" or "weight")) throw new DomainValidationException("Unit type must be EACH or WEIGHT.");
        var each = unitType.Equals("EACH", StringComparison.OrdinalIgnoreCase);
        if (each && value != decimal.Truncate(value)) throw new DomainValidationException("EACH quantity must be a whole number.");
        if (!each && decimal.Round(value, 3) != value) throw new DomainValidationException("WEIGHT quantities support at most three decimal places.");
    }
}

public sealed class LowStockAlertEntity
{
    private LowStockAlertEntity() { } // EF Core

    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }
    public DateTimeOffset RaisedAt { get; private set; }
    public bool Acknowledged { get; private set; }

    public void Acknowledge() => Acknowledged = true;
}
