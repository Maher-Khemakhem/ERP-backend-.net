namespace SupermarketErp.Api.Features.Pos;

/// <summary>
/// Persistence for the POS aggregates: loads and stores entities and nothing else. Business rules live in the
/// domain entities and in <see cref="PosService"/>; saving and transactions belong to the unit of work.
/// </summary>
public interface IPosRepository
{
    Task<IReadOnlyList<TillEntity>> ListTills(CancellationToken ct);
    Task<TillEntity?> FindTill(Guid id, CancellationToken ct);

    Task<IReadOnlyList<CashSessionEntity>> ListSessions(CancellationToken ct);
    Task<CashSessionEntity?> FindSession(Guid id, CancellationToken ct);
    /// <summary>Loads the session and locks it until the transaction ends, so tickets changing the drawer queue up.</summary>
    Task<CashSessionEntity?> FindSessionForUpdate(Guid id, CancellationToken ct);
    Task<CashSessionEntity?> FindOpenSession(Guid tillId, CancellationToken ct);
    Task<CashSessionEntity?> FindLatestSession(Guid tillId, CancellationToken ct);

    Task<IReadOnlyList<PosTransactionEntity>> ListTickets(CancellationToken ct);

    /// <summary>The ticket with all its lines, or null.</summary>
    Task<PosTransactionEntity?> FindTicket(Guid id, CancellationToken ct);

    void Add(TillEntity till);
    void Add(CashSessionEntity session);
    void Add(PosTransactionEntity ticket);
    void Add(TransactionLineEntity line);
    void Remove(PosTransactionEntity ticket);
    void Remove(TransactionLineEntity line);
}
