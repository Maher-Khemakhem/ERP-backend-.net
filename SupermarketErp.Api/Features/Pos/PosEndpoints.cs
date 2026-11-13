using System.Globalization;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Features.Auth;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Pos;

public sealed record CreateTillRequest(string TillNumber);
public sealed record OpenSessionRequest(Guid TillId, string CashierName, decimal OpeningFloat);
public sealed record CloseSessionRequest(decimal CountedCash);
public sealed record StartTransactionRequest(Guid CashSessionId);
public sealed record AddLineRequest(Guid ProductId, decimal QuantityValue, string UnitType);
public sealed record AddLineResponse(string LinePrice, string NetPrice, string VatAmount, string LineId);
public sealed record ScannerPairingView(Guid SessionId, string Token, string ExpiresAt);
public sealed record ScannerScanRequest(string Barcode);
public sealed record ScannerScanResult(string Barcode, Guid ProductId, string Sku, string ProductName,
    string UnitType, string Quantity, string LinePrice, string NetPrice, string VatAmount);
public sealed record CompleteTransactionRequest(string PaymentMethod, decimal? AmountReceived);
public sealed record VoidTransactionRequest(string? Reason);
public sealed record TillView(Guid Id, string TillNumber, string Status, string? CashierName,
    string CashInDrawer, string? OpenedAt, string? ClosedAt);
public sealed record SessionView(Guid Id, Guid TillId, string Status, string ExpectedCash);
public sealed record PosLineView(Guid Id, Guid ProductId, string Quantity, string LineTotal, string NetTotal, string VatAmount);
public sealed record TransactionView(Guid Id, Guid CashSessionId, string Status, string Total,
    string NetTotal, string VatTotal, string? PaymentMethod, string StartedAt, string? CompletedAt,
    IReadOnlyList<PosLineView> Lines);

public static class PosEndpoints
{
    public static IEndpointRouteBuilder MapPosEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var pos = endpoints.MapGroup("/api/pos").WithTags("Point of sale");
        pos.MapGet("/tills", async (PosRepository repo, CancellationToken ct) => Results.Ok(await repo.ListTills(ct)))
            .RequireAuthorization(Policies.StaffRead);
        pos.MapGet("/tills/{id:guid}", async (Guid id, PosRepository repo, CancellationToken ct) =>
            await repo.GetTill(id, ct) is { } value ? Results.Ok(value) : Results.NotFound())
            .RequireAuthorization(Policies.StaffRead);
        pos.MapPost("/tills", async (CreateTillRequest request, PosRepository repo, CancellationToken ct) =>
            Results.Ok(new { id = await repo.CreateTill(request, ct) }))
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);
        pos.MapPost("/sessions/open", async (OpenSessionRequest request, PosRepository repo, CancellationToken ct) =>
            Results.Ok(new { cashSessionId = await repo.OpenSession(request, ct) }))
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);
        pos.MapGet("/sessions/active", async (Guid tillId, PosRepository repo, CancellationToken ct) =>
            await repo.GetActiveSession(tillId, ct) is { } session ? Results.Ok(session) : Results.NoContent())
            .RequireAuthorization(Policies.StaffRead);
        pos.MapGet("/sessions/{cashSessionId:guid}", async (Guid cashSessionId, PosRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.GetSession(cashSessionId, ct)))
            .RequireAuthorization(Policies.StaffRead);
        pos.MapPost("/sessions/{cashSessionId:guid}/close", async (Guid cashSessionId, CloseSessionRequest request, PosRepository repo, CancellationToken ct) =>
        {
            await repo.CloseSession(cashSessionId, request, ct);
            return Results.Ok();
        })
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);

        pos.MapGet("/transactions", async (PosRepository repo, CancellationToken ct) => Results.Ok(await repo.ListTransactions(ct)))
            .RequireAuthorization(Policies.StaffRead);
        pos.MapPost("/transactions/start", async (StartTransactionRequest request, PosRepository repo, CancellationToken ct) =>
            Results.Ok(new { transactionId = await repo.StartTransaction(request, ct) }))
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);
        pos.MapGet("/transactions/{id:guid}", async (Guid id, PosRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.GetTransaction(id, ct)))
            .RequireAuthorization(Policies.StaffRead);
        pos.MapPost("/transactions/{id:guid}/lines", async (Guid id, AddLineRequest request, PosRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.AddLine(id, request, ct)))
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);
        pos.MapDelete("/transactions/{id:guid}/lines/{lineId:guid}", async (Guid id, Guid lineId, PosRepository repo, CancellationToken ct) =>
        {
            if (!await repo.RemoveLine(id, lineId, ct)) throw new ArgumentException($"Line not found: {lineId}");
            return Results.Ok();
        })
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);
        pos.MapPost("/transactions/{id:guid}/complete", async (Guid id, CompleteTransactionRequest request, PosRepository repo, CancellationToken ct) =>
        {
            await repo.Complete(id, request, ct);
            return Results.Ok();
        }).RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);
        pos.MapPost("/transactions/{id:guid}/void", async (Guid id, VoidTransactionRequest request, PosRepository repo, CancellationToken ct) =>
        {
            await repo.Void(id, request, ct);
            return Results.Ok();
        })
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);
        pos.MapDelete("/transactions/{id:guid}", async (Guid id, PosRepository repo, CancellationToken ct) =>
        {
            await repo.DeleteTransaction(id, ct);
            return Results.Ok();
        })
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);

        pos.MapPost("/transactions/{id:guid}/scanner-session", async (Guid id, ScannerPairingService scanner, CancellationToken ct) =>
            Results.Ok(await scanner.Create(id, ct)))
            .RequireAuthorization(Roles.Admin, Roles.Manager, Roles.Cashier);
        pos.MapPost("/scanner-sessions/{sessionId:guid}/scans", async (Guid sessionId, ScannerScanRequest request,
            HttpRequest http, ScannerPairingService scanner, CancellationToken ct) =>
            Results.Ok(await scanner.Scan(sessionId, http.Headers["X-Scanner-Token"].FirstOrDefault(), request.Barcode, ct)))
            .AllowAnonymous();
        pos.MapDelete("/scanner-sessions/{sessionId:guid}", (Guid sessionId, HttpRequest http, ScannerPairingService scanner) =>
        {
            scanner.Close(sessionId, http.Headers["X-Scanner-Token"].FirstOrDefault());
            return Results.NoContent();
        }).AllowAnonymous();
        return endpoints;
    }
}

public sealed class PosRepository(BusinessDbContext db)
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
        var p=await db.Products.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==request.ProductId,ct)??throw new ArgumentException($"Product not found: {request.ProductId}");if(!p.UnitType.Equals(unit,StringComparison.Ordinal))throw new ArgumentException("Requested unit type does not match the product.");var price=await db.ProductPrices.AsNoTracking().Where(x=>x.ProductId==request.ProductId).Select(x=>x.UnitPrice).SingleOrDefaultAsync(ct)??throw new ArgumentException($"No price defined for product: {request.ProductId}");var now=DateTimeOffset.UtcNow;var discount=await db.Promotions.AsNoTracking().Where(x=>x.ProductId==request.ProductId&&x.Active&&x.ValidFrom<=now&&x.ValidUntil>=now).OrderBy(x=>x.ValidFrom).Select(x=>(decimal?)x.DiscountPercent).FirstOrDefaultAsync(ct);var net=decimal.Round(price*quantity,3,MidpointRounding.AwayFromZero);if(discount is decimal d)net=decimal.Round(net*(1m-decimal.Round(d/100m,4,MidpointRounding.AwayFromZero)),3,MidpointRounding.AwayFromZero);var vat=decimal.Round(net*(p.VatRate??19m)/100m,3,MidpointRounding.AwayFromZero);var gross=net+vat;var lineId=Guid.NewGuid();db.TransactionLines.Add(new TransactionLineEntity{Id=lineId,TransactionId=transactionId,ProductId=request.ProductId,Quantity=quantity,UnitType=unit=="EACH"?(short)0:(short)1,LineTotal=gross,VatAmount=vat});ticket.Version++;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(FormatMoney(gross),FormatMoney(net),FormatMoney(vat),lineId.ToString());
    }

    public async Task<bool> RemoveLine(Guid transactionId,Guid lineId,CancellationToken ct){await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);var t=await db.PosTransactions.SingleOrDefaultAsync(x=>x.Id==transactionId,ct)??throw new ArgumentException($"Ticket not found: {transactionId}");if(t.Status!="IN_PROGRESS")throw new InvalidOperationException("Only in-progress tickets can be changed.");var line=await db.TransactionLines.SingleOrDefaultAsync(x=>x.Id==lineId&&x.TransactionId==transactionId,ct);if(line is null){await tx.CommitAsync(ct);return false;}db.TransactionLines.Remove(line);t.Version++;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return true;}

    public async Task Complete(Guid id,CompleteTransactionRequest request,CancellationToken ct)
    {
        var method=request.PaymentMethod.Trim().ToUpperInvariant();if(method is not("CASH" or "CARD"))throw new ArgumentException("Payment method must be CASH or CARD.");await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);var t=await db.PosTransactions.SingleOrDefaultAsync(x=>x.Id==id&&x.Status=="IN_PROGRESS",ct)??throw new InvalidOperationException("Only in-progress tickets can be completed.");await RequireOpenSession(t.CashSessionId,ct);var lines=await db.TransactionLines.Where(x=>x.TransactionId==id).ToListAsync(ct);var total=lines.Sum(x=>x.LineTotal);if(request.AmountReceived is decimal tender&&((method=="CASH"&&tender<total)||(method=="CARD"&&tender!=total)))throw new ArgumentException(method=="CASH"?"Cash received is less than the transaction total.":"Card amount must equal the transaction total.");decimal cogs=0;foreach(var line in lines)cogs+=await SellFromStock(line.ProductId,line.Quantity,line.UnitType,ct);var net=lines.Sum(x=>x.LineTotal-x.VatAmount);var vat=lines.Sum(x=>x.VatAmount);t.Status="COMPLETED";t.PaymentMethod=method;t.CompletedAt=DateTimeOffset.UtcNow;t.Version++;if(method=="CASH"){var s=await db.CashSessions.SingleAsync(x=>x.Id==t.CashSessionId,ct);s.ExpectedCash+=total;s.Version++;}await db.SaveChangesAsync(ct);await OutboxWriter.Write(db,"SaleCompleted",new SaleCompletedEvent(id,t.CashSessionId,method,net,vat,total,cogs),ct);await tx.CommitAsync(ct);
    }

    public async Task<bool> Void(Guid id,VoidTransactionRequest request,CancellationToken ct){await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);var t=await db.PosTransactions.SingleOrDefaultAsync(x=>x.Id==id,ct)??throw new ArgumentException($"Ticket not found: {id}");if(t.Status!="IN_PROGRESS")throw new InvalidOperationException("Only in-progress tickets can be changed.");await RequireOpenSession(t.CashSessionId,ct);t.Status="VOIDED";t.Version++;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return true;}
    public async Task<bool> DeleteTransaction(Guid id,CancellationToken ct){await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);var t=await db.PosTransactions.SingleOrDefaultAsync(x=>x.Id==id,ct)??throw new ArgumentException($"Ticket not found: {id}");if(t.Status!="IN_PROGRESS")throw new InvalidOperationException("Only in-progress tickets can be changed.");await RequireOpenSession(t.CashSessionId,ct);db.PosTransactions.Remove(t);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return true;}

    private async Task<decimal> SellFromStock(Guid productId,decimal requested,short unit,CancellationToken ct){var stock=await db.StockLevels.SingleOrDefaultAsync(x=>x.ProductId==productId,ct)??throw new InvalidOperationException($"No stock found for product: {productId}");var lots=await (from e in db.LotEntries join l in db.Lots on e.LotId equals l.Id where e.StockLevelId==stock.Id&&e.OnHandUnitType==unit orderby l.ExpiryDate==null,l.ExpiryDate,l.ReceivedDate,l.Id select new{e,l}).ToListAsync(ct);var available=lots.Sum(x=>(x.e.OnHandValue ?? 0m)-(x.e.ReservedValue ?? 0m));if(available<requested)throw new InvalidOperationException($"Insufficient stock for product {productId}: requested {requested}, available {available}.");var remaining=requested;decimal cost=0;foreach(var x in lots){if(remaining<=0)break;var take=Math.Min(remaining,(x.e.OnHandValue ?? 0m)-(x.e.ReservedValue ?? 0m));if(take<=0)continue;cost=decimal.Round(cost+take*x.l.UnitCost,3,MidpointRounding.AwayFromZero);x.e.OnHandValue=(x.e.OnHandValue ?? 0m)-take;remaining-=take;}stock.Version++;var onHand=await (from e in db.LotEntries where e.StockLevelId==stock.Id select (e.OnHandValue ?? 0m)-(e.ReservedValue ?? 0m)).SumAsync(ct);var product=await db.Products.AsNoTracking().SingleAsync(x=>x.Id==productId,ct);if(onHand<=product.ReorderPoint) { await db.SaveChangesAsync(ct); await OutboxWriter.Write(db,"LowStockThresholdReached",productId,ct); } return cost;}
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

public sealed class ScannerPairingService(BusinessDbContext db, PosRepository pos)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<Guid, Pairing> _sessions = new();

    public async Task<ScannerPairingView> Create(Guid transactionId, CancellationToken ct)
    {
        var transaction = await pos.GetTransaction(transactionId, ct)
            ?? throw new ArgumentException($"Ticket not found: {transactionId}");
        if (transaction.Status != "IN_PROGRESS") throw new InvalidOperationException("Only an in-progress ticket can use the phone scanner.");
        var now = DateTimeOffset.UtcNow;
        foreach (var existing in _sessions.Where(pair => pair.Value.ExpiresAt < now || pair.Value.TransactionId == transactionId))
            _sessions.TryRemove(existing.Key, out _);
        var id = Guid.NewGuid();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var expires = now.Add(Lifetime);
        _sessions[id] = new Pairing(id, token, transactionId, expires);
        return new ScannerPairingView(id, token, JavaInstantFormat.Format(expires));
    }

    public async Task<ScannerScanResult> Scan(Guid sessionId, string? token, string rawBarcode, CancellationToken ct)
    {
        var pairing = Require(sessionId, token);
        var transaction = await pos.GetTransaction(pairing.TransactionId, ct)
            ?? throw new ArgumentException("Scanner ticket no longer exists.");
        if (transaction.Status != "IN_PROGRESS") throw new InvalidOperationException("Only an in-progress ticket can use the phone scanner.");
        var barcode = rawBarcode?.Trim() ?? "";
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Sku.ToLower() == barcode.ToLower(), ct)
            ?? throw new ArgumentException($"No product found for barcode: {barcode}");
        var productId = product.Id;
        var sku = product.Sku;
        var name = product.Name;
        var unit = product.UnitType;
        var quantity = unit == "WEIGHT" ? 0.1m : 1m;
        var line = await pos.AddLine(pairing.TransactionId, new AddLineRequest(productId, quantity, unit), ct);
        return new ScannerScanResult(barcode, productId, sku, name, unit,
            unit == "WEIGHT" ? "0.100 kg" : "1 unit(s)", line.LinePrice, line.NetPrice, line.VatAmount);
    }

    public void Close(Guid sessionId, string? token)
    {
        _ = Require(sessionId, token);
        _sessions.TryRemove(sessionId, out _);
    }

    private Pairing Require(Guid sessionId, string? token)
    {
        if (!_sessions.TryGetValue(sessionId, out var pairing) || pairing.ExpiresAt < DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(sessionId, out _);
            throw new ArgumentException("Scanner pairing has expired or does not exist.");
        }
        if (token is null || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(pairing.Token)))
            throw new ArgumentException("Invalid scanner pairing token.");
        return pairing;
    }

    private sealed record Pairing(Guid Id, string Token, Guid TransactionId, DateTimeOffset ExpiresAt);
}

