using System.Globalization;
using Microsoft.Data.SqlClient;
using SupermarketErp.Api.Features.Auth;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Finance;

public sealed record CreateInvoiceRequest(Guid SupplierId, Guid? PurchaseOrderId, string InvoiceNumber,
    decimal Subtotal, decimal VatRate, DateOnly? IssueDate, DateOnly? DueDate);
public sealed record PayInvoiceRequest(string PaymentReference, decimal Amount, string PaymentMethod, DateOnly? PaidDate);
public sealed record CardSettlementRequest(string SettlementReference, decimal Amount, DateOnly? SettlementDate);
public sealed record LedgerEntryView(Guid Id, string EntryType, string Amount, string Description, Guid? ReferenceId, string CreatedAt);
public sealed record JournalLineView(Guid Id, string Account, string Debit, string Credit, string Description);
public sealed record JournalView(Guid Id, string EventKey, string Description, Guid? ReferenceId, string PostedAt,
    IReadOnlyList<JournalLineView> Lines);
public sealed record ProfitAndLossView(string From, string To, string Revenue, string OutputVat,
    string CostOfGoodsSold, string CashVariance, string GrossProfit, string NetProfit);
public sealed record InvoiceView(Guid Id, Guid SupplierId, Guid? PurchaseOrderId, string InvoiceNumber,
    string Subtotal, string TaxAmount, string Total, string PaidAmount, string Outstanding,
    string Status, string IssueDate, string? DueDate);

public static class FinanceEndpoints
{
    public static IEndpointRouteBuilder MapFinanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var finance = endpoints.MapGroup("/api/finance").WithTags("Finance")
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Accountant);
        finance.MapGet("/ledger", async (FinanceRepository repo, CancellationToken ct) => Results.Ok(await repo.ListLedger(ct)));
        finance.MapGet("/journals", async (FinanceRepository repo, CancellationToken ct) => Results.Ok(await repo.ListJournals(ct)));
        finance.MapGet("/reports/profit-and-loss", async (DateOnly? from, DateOnly? to, FinanceRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ProfitAndLoss(from, to, ct)));
        finance.MapGet("/supplier-invoices", async (FinanceRepository repo, CancellationToken ct) => Results.Ok(await repo.ListInvoices(ct)));
        finance.MapPost("/supplier-invoices", async (CreateInvoiceRequest request, FinanceRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.CreateInvoice(request, ct)));
        finance.MapPost("/supplier-invoices/{invoiceId:guid}/payments", async (Guid invoiceId, PayInvoiceRequest request, FinanceRepository repo, CancellationToken ct) =>
        {
            var invoice = await repo.PayInvoice(invoiceId, request, ct);
            return Results.Ok(invoice ?? throw new ArgumentException($"Supplier invoice not found: {invoiceId}"));
        });
        finance.MapPost("/settlements/card", async (CardSettlementRequest request, FinanceRepository repo, CancellationToken ct) =>
        {
            await repo.SettleCard(request, ct);
            return Results.Ok();
        });
        return endpoints;
    }
}

public sealed class FinanceRepository(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<LedgerEntryView>> ListLedger(CancellationToken ct)
    {
        const string sql = """
            SELECT jl.id,jl.account_code,CASE WHEN jl.debit_amount=0 THEN jl.credit_amount ELSE jl.debit_amount END,
                   je.description || ' - ' || jl.description,je.reference_id,je.posted_at
            FROM finance.journal_entry je JOIN finance.journal_line jl ON jl.journal_entry_id=je.id
            UNION ALL
            SELECT id,type,amount,description,reference_id,recorded_at FROM finance.ledger_entry
            ORDER BY 6 DESC
            """;
        var entries = new List<LedgerEntryView>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) entries.Add(new LedgerEntryView(reader.GetGuid(0), reader.GetString(1),
            FormatMoney(reader.GetDecimal(2)), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetGuid(4), Instant(reader, 5)));
        return entries;
    }

    public async Task<IReadOnlyList<JournalView>> ListJournals(CancellationToken ct)
    {
        const string sql = """
            SELECT je.id,je.event_key,je.description,je.reference_id,je.posted_at,
                   jl.id,jl.account_code,jl.debit_amount,jl.credit_amount,jl.description
            FROM finance.journal_entry je LEFT JOIN finance.journal_line jl ON jl.journal_entry_id=je.id
            ORDER BY je.posted_at DESC,jl.id
            """;
        var entries = new Dictionary<Guid, JournalBuilder>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            if (!entries.TryGetValue(id, out var entry))
            {
                entry = new JournalBuilder(id, reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3), Instant(reader, 4));
                entries.Add(id, entry);
            }
            if (!reader.IsDBNull(5)) entry.Lines.Add(new JournalLineView(reader.GetGuid(5), reader.GetString(6),
                FormatMoney(reader.GetDecimal(7)), FormatMoney(reader.GetDecimal(8)), reader.GetString(9)));
        }
        return entries.Values.Select(x => x.ToView()).ToArray();
    }

    public async Task<ProfitAndLossView> ProfitAndLoss(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var start = from ?? new DateOnly(today.Year, today.Month, 1);
        var end = to ?? today;
        if (end < start) throw new ArgumentException("to cannot be before from");
        const string sql = """
            SELECT jl.account_code,
                SUM(jl.credit_amount-jl.debit_amount)
            FROM finance.journal_line jl
            JOIN finance.journal_entry je ON je.id=jl.journal_entry_id
            WHERE je.posted_at>=@from AND je.posted_at<@until
            GROUP BY jl.account_code
            """;
        var balances = new Dictionary<string, decimal>(StringComparer.Ordinal);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("from", start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        command.Parameters.AddWithValue("until", end.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) balances[reader.GetString(0)] = reader.GetDecimal(1);
        var revenue = balances.GetValueOrDefault("SALES_REVENUE");
        var vat = balances.GetValueOrDefault("OUTPUT_VAT");
        var cogs = -balances.GetValueOrDefault("COST_OF_GOODS_SOLD");
        var variance = balances.GetValueOrDefault("CASH_VARIANCE");
        var gross = revenue - cogs;
        return new ProfitAndLossView(start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"), FormatMoney(revenue),
            FormatMoney(vat), FormatMoney(cogs), FormatMoney(variance), FormatMoney(gross), FormatMoney(gross + variance));
    }

    public async Task<IReadOnlyList<InvoiceView>> ListInvoices(CancellationToken ct)
    {
        var list = new List<InvoiceView>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InvoiceSelect + " ORDER BY created_at DESC", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(ReadInvoice(reader));
        return list;
    }

    public async Task<InvoiceView> CreateInvoice(CreateInvoiceRequest request, CancellationToken ct)
    {
        ValidateInvoice(request);
        var subtotal = Money(request.Subtotal);
        var tax = Money(subtotal * request.VatRate / 100m);
        var total = subtotal + tax;
        var id = Guid.NewGuid();
        var issueDate = request.IssueDate ?? DateOnly.FromDateTime(DateTime.Today);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO finance.supplier_invoice(id,supplier_id,purchase_order_id,invoice_number,subtotal,tax_amount,total_amount,paid_amount,status,issue_date,due_date,created_at,version)
            VALUES(@id,@supplier,@po,@number,@subtotal,@tax,@total,0,'OPEN',@issue,@due,@now,0)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("supplier", request.SupplierId);
            insert.Parameters.AddWithValue("po", (object?)request.PurchaseOrderId ?? DBNull.Value);
            insert.Parameters.AddWithValue("number", request.InvoiceNumber.Trim());
            insert.Parameters.AddWithValue("subtotal", subtotal);
            insert.Parameters.AddWithValue("tax", tax);
            insert.Parameters.AddWithValue("total", total);
            insert.Parameters.AddWithValue("issue", issueDate);
            insert.Parameters.AddWithValue("due", (object?)request.DueDate ?? DBNull.Value);
            insert.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
            await insert.ExecuteNonQueryAsync(ct);
        }
        var lines = new List<JournalLineInput>
        {
            new("GOODS_RECEIVED_NOT_INVOICED", subtotal, 0, "Invoice net amount")
        };
        if (tax != 0) lines.Add(new("INPUT_VAT", tax, 0, "Input VAT"));
        lines.Add(new("ACCOUNTS_PAYABLE", 0, total, "Supplier payable"));
        await PostJournal(connection, transaction, $"supplier-invoice:{id}", $"Supplier invoice {request.InvoiceNumber.Trim()}", id, lines, ct);
        await transaction.CommitAsync(ct);
        return new InvoiceView(id, request.SupplierId, request.PurchaseOrderId, request.InvoiceNumber.Trim(),
            FormatMoney(subtotal), FormatMoney(tax), FormatMoney(total), FormatMoney(0), FormatMoney(total),
            "OPEN", issueDate.ToString("yyyy-MM-dd"), request.DueDate?.ToString("yyyy-MM-dd"));
    }

    public async Task<InvoiceView?> PayInvoice(Guid invoiceId, PayInvoiceRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PaymentReference)) throw new ArgumentException("Payment reference is required.");
        if (request.Amount <= 0) throw new ArgumentException("Payment amount must be positive.");
        if (string.IsNullOrWhiteSpace(request.PaymentMethod)) throw new ArgumentException("Payment method is required.");
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        await using (var duplicate = new NpgsqlCommand("SELECT invoice_id FROM finance.supplier_payment WHERE payment_reference=@reference", connection, transaction))
        {
            duplicate.Parameters.AddWithValue("reference", request.PaymentReference.Trim());
            var existing = await duplicate.ExecuteScalarAsync(ct);
            if (existing is Guid existingInvoice)
            {
                if (existingInvoice != invoiceId) throw new ArgumentException("Payment reference already belongs to another invoice.");
                await transaction.CommitAsync(ct);
                return await GetInvoice(invoiceId, ct);
            }
        }
        var invoice = await ReadInvoiceForUpdate(connection, transaction, invoiceId, ct);
        if (invoice is null) return null;
        if (invoice.Status is "PAID" or "CANCELLED") throw new InvalidOperationException("Invoice cannot accept more payments.");
        var amount = Money(request.Amount);
        if (amount > invoice.Total - invoice.Paid) throw new ArgumentException("Payment exceeds outstanding invoice balance.");
        var paid = invoice.Paid + amount;
        var status = paid == invoice.Total ? "PAID" : "PARTIALLY_PAID";
        var method = request.PaymentMethod.Trim().ToUpperInvariant();
        await using (var payment = new NpgsqlCommand("INSERT INTO finance.supplier_payment(id,payment_reference,invoice_id,amount,payment_method,paid_date) VALUES(@id,@reference,@invoice,@amount,@method,@date)", connection, transaction))
        {
            payment.Parameters.AddWithValue("id", Guid.NewGuid());
            payment.Parameters.AddWithValue("reference", request.PaymentReference.Trim());
            payment.Parameters.AddWithValue("invoice", invoiceId);
            payment.Parameters.AddWithValue("amount", amount);
            payment.Parameters.AddWithValue("method", method);
            payment.Parameters.AddWithValue("date", request.PaidDate ?? DateOnly.FromDateTime(DateTime.Today));
            await payment.ExecuteNonQueryAsync(ct);
        }
        await using (var update = new NpgsqlCommand("UPDATE finance.supplier_invoice SET paid_amount=@paid,status=@status,version=version+1 WHERE id=@id", connection, transaction))
        {
            update.Parameters.AddWithValue("paid", paid);
            update.Parameters.AddWithValue("status", status);
            update.Parameters.AddWithValue("id", invoiceId);
            await update.ExecuteNonQueryAsync(ct);
        }
        var source = method == "CASH" ? "CASH_ON_HAND" : "BANK";
        await PostJournal(connection, transaction, $"supplier-payment:{request.PaymentReference.Trim()}",
            $"Payment for supplier invoice {invoice.InvoiceNumber}", invoiceId,
            [new("ACCOUNTS_PAYABLE", amount, 0, "Reduce supplier payable"), new(source, 0, amount, "Supplier payment")], ct);
        await transaction.CommitAsync(ct);
        return invoice.WithPayment(paid, status);
    }

    public async Task SettleCard(CardSettlementRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SettlementReference)) throw new ArgumentException("Settlement reference is required.");
        if (request.Amount <= 0) throw new ArgumentException("Settlement amount must be positive.");
        var amount = Money(request.Amount);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        await PostJournal(connection, transaction, $"card-settlement:{request.SettlementReference.Trim()}",
            $"Card settlement {request.SettlementReference.Trim()}", null,
            [new("BANK", amount, 0, "Card settlement received"), new("CARD_CLEARING", 0, amount, "Clear card receivable")], ct);
        await transaction.CommitAsync(ct);
    }

    private const string InvoiceSelect = """
        SELECT id,supplier_id,purchase_order_id,invoice_number,subtotal,tax_amount,total_amount,paid_amount,status,issue_date,due_date
        FROM finance.supplier_invoice
        """;

    private async Task<InvoiceView?> GetInvoice(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InvoiceSelect + " WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadInvoice(reader) : null;
    }

    private static async Task<InvoiceRecord?> ReadInvoiceForUpdate(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(InvoiceSelect + " WHERE id=@id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadInvoiceRecord(reader);
    }

    private static InvoiceView ReadInvoice(NpgsqlDataReader reader) => ReadInvoiceRecord(reader).ToView();

    private static InvoiceRecord ReadInvoiceRecord(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1),
        r.IsDBNull(2) ? null : r.GetGuid(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5),
        r.GetDecimal(6), r.GetDecimal(7), r.GetString(8), r.GetFieldValue<DateOnly>(9), r.IsDBNull(10) ? null : r.GetFieldValue<DateOnly>(10));

    private static void ValidateInvoice(CreateInvoiceRequest r)
    {
        if (r.SupplierId == Guid.Empty) throw new ArgumentException("Supplier ID is required.");
        if (string.IsNullOrWhiteSpace(r.InvoiceNumber)) throw new ArgumentException("Invoice number is required.");
        if (r.Subtotal <= 0) throw new ArgumentException("Invoice subtotal must be positive.");
        if (r.VatRate is < 0 or > 100) throw new ArgumentException("VAT rate must be between 0 and 100.");
        if (r.DueDate is not null && r.DueDate < (r.IssueDate ?? DateOnly.FromDateTime(DateTime.Today)))
            throw new ArgumentException("Due date cannot be before the issue date.");
    }

    private static decimal Money(decimal value) => decimal.Round(value, 3, MidpointRounding.AwayFromZero);
    private static string FormatMoney(decimal value) => $"{value.ToString("0.000", CultureInfo.InvariantCulture)} TND";
    private static string Instant(NpgsqlDataReader r, int i) => JavaInstantFormat.Format(new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc)));

    private sealed record JournalLineInput(string Account, decimal Debit, decimal Credit, string Description);
    private sealed class JournalBuilder(Guid id, string key, string description, Guid? reference, string postedAt)
    {
        public List<JournalLineView> Lines { get; } = [];
        public JournalView ToView() => new(id, key, description, reference, postedAt, Lines);
    }

    private sealed record InvoiceRecord(Guid Id, Guid SupplierId, Guid? PurchaseOrderId, string InvoiceNumber,
        decimal Subtotal, decimal Tax, decimal Total, decimal Paid, string Status, DateOnly IssueDate, DateOnly? DueDate)
    {
        public InvoiceView ToView() => new(Id, SupplierId, PurchaseOrderId, InvoiceNumber, FormatMoney(Subtotal),
            FormatMoney(Tax), FormatMoney(Total), FormatMoney(Paid), FormatMoney(Total-Paid), Status,
            IssueDate.ToString("yyyy-MM-dd"), DueDate?.ToString("yyyy-MM-dd"));
        public InvoiceView WithPayment(decimal paid, string status) => (this with { Paid = paid, Status = status }).ToView();
    }

    private static async Task PostJournal(NpgsqlConnection c, NpgsqlTransaction tx, string key, string description,
        Guid? reference, IReadOnlyList<JournalLineInput> lines, CancellationToken ct)
    {
        if (lines.Sum(line => line.Debit) != lines.Sum(line => line.Credit))
            throw new InvalidOperationException($"Journal entry {key} is unbalanced.");
        var id = Guid.NewGuid();
        await using (var entry = new NpgsqlCommand("""
            INSERT INTO finance.journal_entry(id,event_key,description,reference_id,posted_at)
            VALUES(@id,@key,@description,@reference,@now) ON CONFLICT(event_key) DO NOTHING RETURNING id
            """, c, tx))
        {
            entry.Parameters.AddWithValue("id", id);
            entry.Parameters.AddWithValue("key", key);
            entry.Parameters.AddWithValue("description", description);
            entry.Parameters.AddWithValue("reference", (object?)reference ?? DBNull.Value);
            entry.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
            var inserted = await entry.ExecuteScalarAsync(ct);
            if (inserted is null) return;
            id = (Guid)inserted;
        }
        foreach (var line in lines)
        {
            await using var insert = new NpgsqlCommand("INSERT INTO finance.journal_line(id,journal_entry_id,account_code,debit_amount,credit_amount,description) VALUES(@id,@entry,@account,@debit,@credit,@description)", c, tx);
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("entry", id);
            insert.Parameters.AddWithValue("account", line.Account);
            insert.Parameters.AddWithValue("debit", line.Debit);
            insert.Parameters.AddWithValue("credit", line.Credit);
            insert.Parameters.AddWithValue("description", line.Description);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }
}
