namespace SupermarketErp.Api.Features.Pos;

public static class TillStatus
{
    public const string Open = "OPEN";
    public const string Closed = "CLOSED";
}

public static class SessionStatus
{
    public const string Open = "OPEN";
    public const string Closed = "CLOSED";
}

public static class TicketStatus
{
    public const string InProgress = "IN_PROGRESS";
    public const string Completed = "COMPLETED";
    public const string Voided = "VOIDED";
}

public static class PaymentMethods
{
    public const string Cash = "CASH";
    public const string Card = "CARD";

    /// <summary>Normalizes user input ("  cash ") to a known payment method.</summary>
    public static string Parse(string? raw)
    {
        var method = raw?.Trim().ToUpperInvariant();
        return method is Cash or Card ? method : throw new DomainValidationException("Payment method must be CASH or CARD.");
    }
}

/// <summary>A cash register. It is open exactly while it has an open cash session.</summary>
public sealed class TillEntity
{
    private TillEntity() { } // EF Core

    public Guid Id { get; private set; }
    public string TillNumber { get; private set; } = "";
    public string Status { get; private set; } = TillStatus.Closed;
    public long Version { get; private set; }

    public static TillEntity Create(string? tillNumber)
    {
        if (string.IsNullOrWhiteSpace(tillNumber)) throw new DomainValidationException("Till number is required.");
        return new TillEntity { Id = Guid.NewGuid(), TillNumber = tillNumber.Trim(), Status = TillStatus.Closed };
    }

    public void MarkOpen() { Status = TillStatus.Open; Version++; }
    public void MarkClosed() { Status = TillStatus.Closed; Version++; }
}

/// <summary>A cashier's shift on a till: from the opening float to the counted cash at close.</summary>
public sealed class CashSessionEntity
{
    private CashSessionEntity() { } // EF Core

    public Guid Id { get; private set; }
    public Guid TillId { get; private set; }
    public string CashierName { get; private set; } = "";
    public decimal OpeningFloat { get; private set; }
    public decimal ExpectedCash { get; private set; }
    public decimal? CountedCash { get; private set; }
    public string Status { get; private set; } = SessionStatus.Open;
    public DateTimeOffset OpenedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public long Version { get; private set; }

    public bool IsOpen => Status == SessionStatus.Open;

    public static CashSessionEntity Open(Guid tillId, string? cashierName, decimal openingFloat, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(cashierName)) throw new DomainValidationException("Cashier name is required.");
        if (openingFloat < 0) throw new DomainValidationException("Opening float cannot be negative.");
        return new CashSessionEntity
        {
            Id = Guid.NewGuid(), TillId = tillId, CashierName = cashierName.Trim(), OpeningFloat = openingFloat,
            ExpectedCash = openingFloat, Status = SessionStatus.Open, OpenedAt = now
        };
    }

    public void RequireOpen()
    {
        if (!IsOpen) throw new ConflictException("Cash session is not open.");
    }

    /// <summary>Cash taken from a customer goes into the drawer.</summary>
    public void RecordCashTakings(decimal amount)
    {
        ExpectedCash += amount;
        Version++;
    }

    /// <summary>Closes the session and returns the cash that was expected in the drawer.</summary>
    public decimal Close(decimal countedCash, DateTimeOffset now)
    {
        if (countedCash < 0) throw new DomainValidationException("Counted cash cannot be negative.");
        if (!IsOpen) throw new ConflictException($"Cash session is not open: {Id}");
        var expected = ExpectedCash;
        CountedCash = countedCash;
        Status = SessionStatus.Closed;
        ClosedAt = now;
        Version++;
        return expected;
    }
}

/// <summary>A customer's ticket: lines are added while it is in progress, then it is completed or voided.</summary>
public sealed class PosTransactionEntity
{
    private PosTransactionEntity() { } // EF Core

    public Guid Id { get; private set; }
    public Guid CashSessionId { get; private set; }
    public string Status { get; private set; } = TicketStatus.InProgress;
    public string? PaymentMethod { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public long Version { get; private set; }
    public List<TransactionLineEntity> Lines { get; private set; } = [];

    public bool IsInProgress => Status == TicketStatus.InProgress;
    public decimal Total => Lines.Sum(x => x.LineTotal);
    public decimal VatTotal => Lines.Sum(x => x.VatAmount);
    public decimal NetTotal => Total - VatTotal;

    /// <summary>Starts a ticket on an open session. A missing session counts as not open.</summary>
    public static PosTransactionEntity Start(CashSessionEntity? session, DateTimeOffset now)
    {
        if (session is not { IsOpen: true }) throw new ConflictException("Cash session is not open.");
        return new PosTransactionEntity
        {
            Id = Guid.NewGuid(), CashSessionId = session.Id, Status = TicketStatus.InProgress, StartedAt = now
        };
    }

    /// <summary>Lines can only be added while the ticket is in progress.</summary>
    public void RequireAcceptsNewLines()
    {
        if (!IsInProgress) throw new DomainValidationException("Ticket not found or is not in progress.");
    }

    public TransactionLineEntity AddLine(Guid productId, decimal quantity, string unitType, decimal lineTotal, decimal vatAmount)
    {
        RequireAcceptsNewLines();
        var line = TransactionLineEntity.Create(Id, productId, quantity, unitType, lineTotal, vatAmount);
        Lines.Add(line);
        Version++;
        return line;
    }

    /// <summary>Removes a line and returns it, or returns null when the ticket has no such line.</summary>
    public TransactionLineEntity? RemoveLine(Guid lineId)
    {
        RequireInProgress();
        var line = Lines.SingleOrDefault(x => x.Id == lineId);
        if (line is null) return null;
        Lines.Remove(line);
        Version++;
        return line;
    }

    public void RequireInProgress()
    {
        if (!IsInProgress) throw new ConflictException("Only in-progress tickets can be changed.");
    }

    public void Void()
    {
        RequireInProgress();
        Status = TicketStatus.Voided;
        Version++;
    }

    public void RequireCanComplete()
    {
        if (!IsInProgress) throw new ConflictException("Only in-progress tickets can be completed.");
    }

    /// <summary>Cash must cover the total; a card payment must match it exactly. No amount means no check.</summary>
    public void RequireTenderCoversTotal(string method, decimal? amountReceived)
    {
        if (amountReceived is not decimal tender) return;
        if (method == PaymentMethods.Cash && tender < Total)
            throw new DomainValidationException("Cash received is less than the transaction total.");
        if (method == PaymentMethods.Card && tender != Total)
            throw new DomainValidationException("Card amount must equal the transaction total.");
    }

    public void Complete(string method, DateTimeOffset now)
    {
        RequireCanComplete();
        Status = TicketStatus.Completed;
        PaymentMethod = method;
        CompletedAt = now;
        Version++;
    }
}

public sealed class TransactionLineEntity
{
    private TransactionLineEntity() { } // EF Core

    public Guid Id { get; private set; }
    public Guid TransactionId { get; private set; }
    public Guid ProductId { get; private set; }
    public decimal Quantity { get; private set; }
    public short UnitType { get; private set; }
    public decimal LineTotal { get; private set; }
    public decimal VatAmount { get; private set; }

    public decimal NetTotal => LineTotal - VatAmount;
    public string UnitName => UnitType == 0 ? "EACH" : "WEIGHT";

    internal static TransactionLineEntity Create(Guid ticketId, Guid productId, decimal quantity, string unitType, decimal lineTotal, decimal vatAmount) =>
        new()
        {
            Id = Guid.NewGuid(), TransactionId = ticketId, ProductId = productId, Quantity = quantity,
            UnitType = unitType == "EACH" ? (short)0 : (short)1, LineTotal = lineTotal, VatAmount = vatAmount
        };

    /// <summary>Validates a requested quantity and unit; weights are kept to three decimals (grams).</summary>
    public static (decimal Quantity, string Unit) NormalizeQuantity(decimal value, string? unitType)
    {
        var unit = unitType?.Trim().ToUpperInvariant();
        if (value <= 0) throw new DomainValidationException("Quantity must be positive.");
        if (unit is not ("EACH" or "WEIGHT")) throw new DomainValidationException("Unit type must be EACH or WEIGHT.");
        if (unit == "EACH" && value != decimal.Truncate(value)) throw new DomainValidationException("EACH quantity must be a whole number.");
        return (unit == "WEIGHT" ? decimal.Round(value, 3, MidpointRounding.AwayFromZero) : value, unit);
    }
}
