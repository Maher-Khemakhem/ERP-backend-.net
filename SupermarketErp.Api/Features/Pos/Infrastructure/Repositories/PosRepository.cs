using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Features.Auth;
using SupermarketErp.Api.Features.Pricing;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Pos;

public sealed class PosRepository(
    BusinessDbContext db,
    IPricingCalculator pricing,
    InventorySaleService inventory) : IPosRepository
{
    public async Task<IReadOnlyList<TillView>> ListTills(CancellationToken ct)
    {
        var tills = await db.Tills.AsNoTracking().OrderBy(x => x.TillNumber).ToListAsync(ct);
        var sessions = await db.CashSessions.AsNoTracking().ToListAsync(ct);
        return tills.Select(t =>
        {
            var s = sessions.Where(x => x.TillId == t.Id).OrderByDescending(x => x.OpenedAt).FirstOrDefault();
            return new TillView(t.Id, t.TillNumber, t.Status, s?.CashierName,
                FormatMoney(s is { Status: "OPEN" } ? s.ExpectedCash : s?.CountedCash ?? 0m),
                s is null ? null : JavaInstantFormat.Format(s.OpenedAt),
                s?.ClosedAt is null ? null : JavaInstantFormat.Format(s.ClosedAt.Value));
        }).ToList();
    }

    public async Task<TillView?> GetTill(Guid id, CancellationToken ct)
    {
        var t = await db.Tills.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (t is null) return null;
        var s = await db.CashSessions.AsNoTracking().Where(x => x.TillId == id).OrderByDescending(x => x.OpenedAt).FirstOrDefaultAsync(ct);
        return new TillView(t.Id, t.TillNumber, t.Status, s?.CashierName, FormatMoney(s is { Status: "OPEN" } ? s.ExpectedCash : s?.CountedCash ?? 0m), s is null ? null : JavaInstantFormat.Format(s.OpenedAt), s?.ClosedAt is null ? null : JavaInstantFormat.Format(s.ClosedAt.Value));
    }

    public async Task<Guid> CreateTill(CreateTillRequest request, CancellationToken ct)
    { if (string.IsNullOrWhiteSpace(request.TillNumber)) throw new ArgumentException("Till number is required."); var id=Guid.NewGuid(); db.Tills.Add(new TillEntity { Id=id, TillNumber=request.TillNumber.Trim(), Status="CLOSED" }); await db.SaveChangesAsync(ct); return id; }

    public async Task<Guid> OpenSession(OpenSessionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CashierName)) throw new ArgumentException("Cashier name is required."); if (request.OpeningFloat < 0) throw new ArgumentException("Opening float cannot be negative.");
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var till=await db.Tills.SingleOrDefaultAsync(x=>x.Id==request.TillId,ct) ?? throw new ArgumentException($"Till not found: {request.TillId}");
        if (await db.CashSessions.AnyAsync(x=>x.TillId==request.TillId && x.Status=="OPEN",ct)) throw new InvalidOperationException("Till already has an open cash session.");
        var id=Guid.NewGuid(); db.CashSessions.Add(new CashSessionEntity { Id=id,TillId=request.TillId,CashierName=request.CashierName.Trim(),OpeningFloat=request.OpeningFloat,ExpectedCash=request.OpeningFloat,Status="OPEN",OpenedAt=DateTimeOffset.UtcNow }); till.Status="OPEN"; till.Version++;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return id;
    }

    public async Task<SessionView> GetSession(Guid id, CancellationToken ct) { var s=await db.CashSessions.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct) ?? throw new ArgumentException($"Cash session not found: {id}"); return Session(s); }
    public async Task<SessionView?> GetActiveSession(Guid tillId, CancellationToken ct) { var s=await db.CashSessions.AsNoTracking().Where(x=>x.TillId==tillId&&x.Status=="OPEN").OrderByDescending(x=>x.OpenedAt).FirstOrDefaultAsync(ct); return s is null?null:Session(s); }

    public async Task<bool> CloseSession(Guid id, CloseSessionRequest request, CancellationToken ct)
    {
        if(request.CountedCash<0) throw new ArgumentException("Counted cash cannot be negative."); await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
        var s=await db.CashSessions.SingleOrDefaultAsync(x=>x.Id==id,ct) ?? throw new ArgumentException($"Cash session not found: {id}"); if(s.Status!="OPEN") throw new InvalidOperationException($"Cash session is not open: {id}"); var expected=s.ExpectedCash;
        s.CountedCash=request.CountedCash;s.Status="CLOSED";s.ClosedAt=DateTimeOffset.UtcNow;s.Version++; var till=await db.Tills.SingleAsync(x=>x.Id==s.TillId,ct);till.Status="CLOSED";till.Version++;
        await db.SaveChangesAsync(ct); await OutboxWriter.Write(db, "CashSessionClosed", new CashSessionClosedEvent(id,expected,request.CountedCash),ct); await tx.CommitAsync(ct); return true;
    }

    public async Task<Guid> StartTransaction(StartTransactionRequest request, CancellationToken ct)
    { await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct); var s=await db.CashSessions.SingleOrDefaultAsync(x=>x.Id==request.CashSessionId,ct); if(s?.Status!="OPEN") throw new InvalidOperationException("Cash session is not open."); var id=Guid.NewGuid();db.PosTransactions.Add(new PosTransactionEntity{Id=id,CashSessionId=request.CashSessionId,Status="IN_PROGRESS",StartedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return id; }
    public async Task<IReadOnlyList<TransactionView>> ListTransactions(CancellationToken ct) { var ids=await db.PosTransactions.AsNoTracking().OrderByDescending(x=>x.StartedAt).Select(x=>x.Id).ToListAsync(ct); var list=new List<TransactionView>(); foreach(var id in ids) list.Add(await GetTransaction(id,ct)); return list; }
    public async Task<TransactionView> GetTransaction(Guid id,CancellationToken ct) { var t=await db.PosTransactions.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw new ArgumentException($"Ticket not found: {id}"); var lines=await ReadLines(id,ct);var total=lines.Sum(x=>ParseMoney(x.LineTotal));var vat=lines.Sum(x=>ParseMoney(x.VatAmount));return new(id,t.CashSessionId,t.Status,FormatMoney(total),FormatMoney(total-vat),FormatMoney(vat),t.PaymentMethod,JavaInstantFormat.Format(t.StartedAt),t.CompletedAt is null?null:JavaInstantFormat.Format(t.CompletedAt.Value),lines); }

    public async Task<AddLineResponse> AddLine(Guid transactionId, AddLineRequest request, CancellationToken ct)
    {
        ValidateQuantity(request.QuantityValue,request.UnitType);var unit=request.UnitType.Trim().ToUpperInvariant();var quantity=unit=="WEIGHT"?decimal.Round(request.QuantityValue,3,MidpointRounding.AwayFromZero):request.QuantityValue;
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);var ticket=await db.PosTransactions.SingleOrDefaultAsync(x=>x.Id==transactionId&&x.Status=="IN_PROGRESS",ct)??throw new ArgumentException("Ticket not found or is not in progress.");await RequireOpenSession(ticket.CashSessionId,ct);
        var calculated = await pricing.CalculateLine(request.ProductId, quantity, unit, ct);
        var lineId=Guid.NewGuid();db.TransactionLines.Add(new TransactionLineEntity{Id=lineId,TransactionId=transactionId,ProductId=request.ProductId,Quantity=quantity,UnitType=unit=="EACH"?(short)0:(short)1,LineTotal=calculated.Gross,VatAmount=calculated.Vat});ticket.Version++;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(FormatMoney(calculated.Gross),FormatMoney(calculated.Net),FormatMoney(calculated.Vat),lineId.ToString());
    }

    public async Task<bool> RemoveLine(Guid transactionId,Guid lineId,CancellationToken ct){await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);var t=await db.PosTransactions.SingleOrDefaultAsync(x=>x.Id==transactionId,ct)??throw new ArgumentException($"Ticket not found: {transactionId}");if(t.Status!="IN_PROGRESS")throw new InvalidOperationException("Only in-progress tickets can be changed.");var line=await db.TransactionLines.SingleOrDefaultAsync(x=>x.Id==lineId&&x.TransactionId==transactionId,ct);if(line is null){await tx.CommitAsync(ct);return false;}db.TransactionLines.Remove(line);t.Version++;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return true;}

    public async Task Complete(Guid id,CompleteTransactionRequest request,CancellationToken ct)
    {
        var method=request.PaymentMethod.Trim().ToUpperInvariant();if(method is not("CASH" or "CARD"))throw new ArgumentException("Payment method must be CASH or CARD.");await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);var t=await db.PosTransactions.SingleOrDefaultAsync(x=>x.Id==id&&x.Status=="IN_PROGRESS",ct)??throw new InvalidOperationException("Only in-progress tickets can be completed.");await RequireOpenSession(t.CashSessionId,ct);var lines=await db.TransactionLines.Where(x=>x.TransactionId==id).ToListAsync(ct);var total=lines.Sum(x=>x.LineTotal);if(request.AmountReceived is decimal tender&&((method=="CASH"&&tender<total)||(method=="CARD"&&tender!=total)))throw new ArgumentException(method=="CASH"?"Cash received is less than the transaction total.":"Card amount must equal the transaction total.");decimal cogs=0;foreach(var line in lines)cogs+=await inventory.Sell(line.ProductId,line.Quantity,line.UnitType,ct);var net=lines.Sum(x=>x.LineTotal-x.VatAmount);var vat=lines.Sum(x=>x.VatAmount);t.Status="COMPLETED";t.PaymentMethod=method;t.CompletedAt=DateTimeOffset.UtcNow;t.Version++;if(method=="CASH"){var s=await db.CashSessions.SingleAsync(x=>x.Id==t.CashSessionId,ct);s.ExpectedCash+=total;s.Version++;}await db.SaveChangesAsync(ct);await OutboxWriter.Write(db,"SaleCompleted",new SaleCompletedEvent(id,t.CashSessionId,method,net,vat,total,cogs),ct);await tx.CommitAsync(ct);
    }

    public async Task<bool> Void(Guid id,VoidTransactionRequest request,CancellationToken ct){await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);var t=await db.PosTransactions.SingleOrDefaultAsync(x=>x.Id==id,ct)??throw new ArgumentException($"Ticket not found: {id}");if(t.Status!="IN_PROGRESS")throw new InvalidOperationException("Only in-progress tickets can be changed.");await RequireOpenSession(t.CashSessionId,ct);t.Status="VOIDED";t.Version++;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return true;}
    public async Task<bool> DeleteTransaction(Guid id,CancellationToken ct){await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);var t=await db.PosTransactions.SingleOrDefaultAsync(x=>x.Id==id,ct)??throw new ArgumentException($"Ticket not found: {id}");if(t.Status!="IN_PROGRESS")throw new InvalidOperationException("Only in-progress tickets can be changed.");await RequireOpenSession(t.CashSessionId,ct);db.PosTransactions.Remove(t);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return true;}

    private async Task<IReadOnlyList<PosLineView>> ReadLines(Guid id,CancellationToken ct)
    {
        var lines = await db.TransactionLines.AsNoTracking().Where(x => x.TransactionId == id).OrderBy(x => x.Id).ToListAsync(ct);
        return lines.Select(x => new PosLineView(x.Id, x.ProductId,
            x.UnitType == 0 ? $"{x.Quantity:0} unit(s)" : $"{x.Quantity:0.000} kg",
            FormatMoney(x.LineTotal), FormatMoney(x.LineTotal - x.VatAmount), FormatMoney(x.VatAmount))).ToList();
    }
    private async Task RequireOpenSession(Guid id,CancellationToken ct){var s=await db.CashSessions.SingleOrDefaultAsync(x=>x.Id==id,ct)??throw new ArgumentException($"Cash session not found: {id}");if(s.Status!="OPEN")throw new InvalidOperationException("Cash session is not open.");}
    private static SessionView Session(CashSessionEntity s)=>new(s.Id,s.TillId,s.Status,FormatMoney(s.ExpectedCash));
    private static void ValidateQuantity(decimal value,string unit){unit=unit.Trim().ToUpperInvariant();if(value<=0)throw new ArgumentException("Quantity must be positive.");if(unit is not("EACH" or "WEIGHT"))throw new ArgumentException("Unit type must be EACH or WEIGHT.");if(unit=="EACH"&&value!=decimal.Truncate(value))throw new ArgumentException("EACH quantity must be a whole number.");}
    private static string FormatMoney(decimal amount)=>$"{amount.ToString("0.000",CultureInfo.InvariantCulture)} TND";private static decimal ParseMoney(string value)=>decimal.Parse(value[..^4],CultureInfo.InvariantCulture);
}


