using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Pos;

public sealed class PosRepository(BusinessDbContext db) : IPosRepository
{
    public async Task<IReadOnlyList<TillEntity>> ListTills(CancellationToken ct) =>
        await db.Tills.AsNoTracking().OrderBy(x => x.TillNumber).ToListAsync(ct);

    public Task<TillEntity?> FindTill(Guid id, CancellationToken ct) =>
        db.Tills.SingleOrDefaultAsync(x => x.Id == id, ct);

    public async Task<IReadOnlyList<CashSessionEntity>> ListSessions(CancellationToken ct) =>
        await db.CashSessions.AsNoTracking().ToListAsync(ct);

    public Task<CashSessionEntity?> FindSession(Guid id, CancellationToken ct) =>
        db.CashSessions.SingleOrDefaultAsync(x => x.Id == id, ct);

    public async Task<CashSessionEntity?> FindSessionForUpdate(Guid id, CancellationToken ct) =>
        (await db.CashSessions
            .FromSqlInterpolated($"SELECT * FROM pos.cash_session WITH (UPDLOCK, HOLDLOCK) WHERE id = {id}")
            .ToListAsync(ct)).SingleOrDefault();

    public Task<CashSessionEntity?> FindOpenSession(Guid tillId, CancellationToken ct) =>
        db.CashSessions.Where(x => x.TillId == tillId && x.Status == SessionStatus.Open)
            .OrderByDescending(x => x.OpenedAt).FirstOrDefaultAsync(ct);

    public Task<CashSessionEntity?> FindLatestSession(Guid tillId, CancellationToken ct) =>
        db.CashSessions.AsNoTracking().Where(x => x.TillId == tillId)
            .OrderByDescending(x => x.OpenedAt).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<PosTransactionEntity>> ListTickets(CancellationToken ct) =>
        await db.PosTransactions.AsNoTracking().Include(x => x.Lines.OrderBy(l => l.Id))
            .OrderByDescending(x => x.StartedAt).ToListAsync(ct);

    public Task<PosTransactionEntity?> FindTicket(Guid id, CancellationToken ct) =>
        db.PosTransactions.Include(x => x.Lines.OrderBy(l => l.Id)).SingleOrDefaultAsync(x => x.Id == id, ct);

    public void Add(TillEntity till) => db.Tills.Add(till);
    public void Add(CashSessionEntity session) => db.CashSessions.Add(session);
    public void Add(PosTransactionEntity ticket) => db.PosTransactions.Add(ticket);
    public void Add(TransactionLineEntity line) => db.TransactionLines.Add(line);
    public void Remove(PosTransactionEntity ticket) => db.PosTransactions.Remove(ticket);
    public void Remove(TransactionLineEntity line) => db.TransactionLines.Remove(line);
}
