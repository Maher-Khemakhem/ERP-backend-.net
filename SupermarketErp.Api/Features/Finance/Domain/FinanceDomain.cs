namespace SupermarketErp.Api.Features.Finance;

/// <summary>The chart of accounts used by the automatic postings.</summary>
public static class AccountCodes
{
    public const string CashOnHand = "CASH_ON_HAND";
    public const string Bank = "BANK";
    public const string CardClearing = "CARD_CLEARING";
    public const string SalesRevenue = "SALES_REVENUE";
    public const string OutputVat = "OUTPUT_VAT";
    public const string InputVat = "INPUT_VAT";
    public const string CostOfGoodsSold = "COST_OF_GOODS_SOLD";
    public const string Inventory = "INVENTORY";
    public const string GoodsReceivedNotInvoiced = "GOODS_RECEIVED_NOT_INVOICED";
    public const string AccountsPayable = "ACCOUNTS_PAYABLE";
    public const string CashVariance = "CASH_VARIANCE";
}

public static class InvoiceStatus
{
    public const string Open = "OPEN";
    public const string PartiallyPaid = "PARTIALLY_PAID";
    public const string Paid = "PAID";
    public const string Cancelled = "CANCELLED";
}

public sealed record JournalLineInput(string Account, decimal Debit, decimal Credit, string Description);

/// <summary>A balanced double-entry posting. <c>EventKey</c> makes a posting idempotent: the same key is only booked once.</summary>
public sealed class JournalEntryEntity
{
    private JournalEntryEntity() { } // EF Core

    public Guid Id { get; private set; }
    public string EventKey { get; private set; } = "";
    public string Description { get; private set; } = "";
    public Guid? ReferenceId { get; private set; }
    public DateTimeOffset PostedAt { get; private set; }
    public List<JournalLineEntity> Lines { get; private set; } = [];

    /// <summary>Creates a posting. Total debits must equal total credits.</summary>
    public static JournalEntryEntity Post(string eventKey, string description, Guid? referenceId,
        IReadOnlyList<JournalLineInput> lines, DateTimeOffset now)
    {
        if (lines.Sum(x => x.Debit) != lines.Sum(x => x.Credit))
            throw new ConflictException($"Journal entry {eventKey} is unbalanced.");
        var entry = new JournalEntryEntity
        {
            Id = Guid.NewGuid(), EventKey = eventKey, Description = description, ReferenceId = referenceId, PostedAt = now
        };
        entry.Lines.AddRange(lines.Select(x => JournalLineEntity.Create(entry.Id, x)));
        return entry;
    }
}

public sealed class JournalLineEntity
{
    private JournalLineEntity() { } // EF Core

    public Guid Id { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public string AccountCode { get; private set; } = "";
    public decimal DebitAmount { get; private set; }
    public decimal CreditAmount { get; private set; }
    public string Description { get; private set; } = "";

    internal static JournalLineEntity Create(Guid entryId, JournalLineInput line) =>
        new()
        {
            Id = Guid.NewGuid(), JournalEntryId = entryId, AccountCode = line.Account,
            DebitAmount = line.Debit, CreditAmount = line.Credit, Description = line.Description
        };
}

/// <summary>A legacy ledger row, only read (the journal is the book of record).</summary>
public sealed class LedgerEntryEntity
{
    private LedgerEntryEntity() { } // EF Core

    public Guid Id { get; private set; }
    public string Type { get; private set; } = "";
    public decimal Amount { get; private set; }
    public string Description { get; private set; } = "";
    public Guid? ReferenceId { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }
}

/// <summary>A supplier's invoice that we owe. It is open until it is paid in one or several payments.</summary>
public sealed class SupplierInvoiceEntity
{
    private SupplierInvoiceEntity() { } // EF Core

    public Guid Id { get; private set; }
    public Guid SupplierId { get; private set; }
    public Guid? PurchaseOrderId { get; private set; }
    public string InvoiceNumber { get; private set; } = "";
    public decimal Subtotal { get; private set; }
    public decimal TaxAmount { get; private set; }
    public decimal TotalAmount { get; private set; }
    public decimal PaidAmount { get; private set; }
    public string Status { get; private set; } = InvoiceStatus.Open;
    public DateOnly IssueDate { get; private set; }
    public DateOnly? DueDate { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public long Version { get; private set; }

    public decimal Outstanding => TotalAmount - PaidAmount;

    public static SupplierInvoiceEntity Create(Guid supplierId, Guid? purchaseOrderId, string? invoiceNumber, decimal subtotal,
        decimal vatRate, DateOnly? issueDate, DateOnly? dueDate, DateOnly today, DateTimeOffset now)
    {
        if (supplierId == Guid.Empty || string.IsNullOrWhiteSpace(invoiceNumber) || subtotal <= 0 || vatRate is < 0 or > 100)
            throw new DomainValidationException("Invalid supplier invoice.");
        var issued = issueDate ?? today;
        if (dueDate is not null && dueDate < issued) throw new DomainValidationException("Due date cannot be before the issue date.");

        var net = Money(subtotal);
        var tax = Money(net * vatRate / 100m);
        return new SupplierInvoiceEntity
        {
            Id = Guid.NewGuid(), SupplierId = supplierId, PurchaseOrderId = purchaseOrderId, InvoiceNumber = invoiceNumber.Trim(),
            Subtotal = net, TaxAmount = tax, TotalAmount = net + tax, Status = InvoiceStatus.Open,
            IssueDate = issued, DueDate = dueDate, CreatedAt = now
        };
    }

    /// <summary>The accounting entry for receiving the invoice: the goods become a payable.</summary>
    public IReadOnlyList<JournalLineInput> PostingLines()
    {
        var lines = new List<JournalLineInput> { new(AccountCodes.GoodsReceivedNotInvoiced, Subtotal, 0, "Invoice net amount") };
        if (TaxAmount != 0) lines.Add(new(AccountCodes.InputVat, TaxAmount, 0, "Input VAT"));
        lines.Add(new(AccountCodes.AccountsPayable, 0, TotalAmount, "Supplier payable"));
        return lines;
    }

    /// <summary>Applies a payment: not more than is owed, and not on an invoice that is already settled or cancelled.</summary>
    public void ApplyPayment(decimal amount)
    {
        if (Status is InvoiceStatus.Paid or InvoiceStatus.Cancelled)
            throw new ConflictException("Invoice cannot accept more payments.");
        if (amount > Outstanding) throw new DomainValidationException("Payment exceeds outstanding invoice balance.");
        PaidAmount += amount;
        Status = PaidAmount == TotalAmount ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;
        Version++;
    }

    public static decimal Money(decimal value) => decimal.Round(value, 3, MidpointRounding.AwayFromZero);
}

public sealed class SupplierPaymentEntity
{
    private SupplierPaymentEntity() { } // EF Core

    public Guid Id { get; private set; }
    public string PaymentReference { get; private set; } = "";
    public Guid InvoiceId { get; private set; }
    public decimal Amount { get; private set; }
    public string PaymentMethod { get; private set; } = "";
    public DateOnly PaidDate { get; private set; }

    public bool IsCash => PaymentMethod == "CASH";

    /// <summary>A payment needs a reference (its idempotency key), a positive amount and a method.</summary>
    public static void ValidateRequest(string? reference, decimal amount, string? method)
    {
        if (string.IsNullOrWhiteSpace(reference) || amount <= 0 || string.IsNullOrWhiteSpace(method))
            throw new DomainValidationException("Payment reference, positive amount and payment method are required.");
    }

    public static SupplierPaymentEntity Create(string reference, Guid invoiceId, decimal amount, string method, DateOnly paidDate) =>
        new()
        {
            Id = Guid.NewGuid(), PaymentReference = reference.Trim(), InvoiceId = invoiceId, Amount = amount,
            PaymentMethod = method.Trim().ToUpperInvariant(), PaidDate = paidDate
        };

    /// <summary>Paying a supplier reduces what we owe, and takes the money from the drawer (cash) or the bank.</summary>
    public IReadOnlyList<JournalLineInput> PostingLines() =>
    [
        new(AccountCodes.AccountsPayable, Amount, 0, "Reduce supplier payable"),
        new(IsCash ? AccountCodes.CashOnHand : AccountCodes.Bank, 0, Amount, "Supplier payment")
    ];
}

/// <summary>The bank paid out the card takings: they move from card clearing to the bank.</summary>
public static class CardSettlement
{
    public static void Validate(string? reference, decimal amount)
    {
        if (string.IsNullOrWhiteSpace(reference) || amount <= 0)
            throw new DomainValidationException("Settlement reference and positive amount are required.");
    }

    public static IReadOnlyList<JournalLineInput> PostingLines(decimal amount) =>
    [
        new(AccountCodes.Bank, amount, 0, "Card settlement received"),
        new(AccountCodes.CardClearing, 0, amount, "Clear card receivable")
    ];
}
