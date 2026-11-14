namespace SupermarketErp.Api.Features.Pos;

public interface IPosService
{
    Task<IReadOnlyList<TillView>> ListTills(CancellationToken ct);
    Task<TillView?> GetTill(Guid id, CancellationToken ct);
    Task<Guid> CreateTill(CreateTillRequest request, CancellationToken ct);
    Task<Guid> OpenSession(OpenSessionRequest request, CancellationToken ct);
    Task<SessionView> GetSession(Guid id, CancellationToken ct);
    Task<SessionView?> GetActiveSession(Guid tillId, CancellationToken ct);
    Task<bool> CloseSession(Guid id, CloseSessionRequest request, CancellationToken ct);
    Task<Guid> StartTransaction(StartTransactionRequest request, CancellationToken ct);
    Task<IReadOnlyList<TransactionView>> ListTransactions(CancellationToken ct);
    Task<TransactionView> GetTransaction(Guid id, CancellationToken ct);
    Task<AddLineResponse> AddLine(Guid transactionId, AddLineRequest request, CancellationToken ct);
    Task<bool> RemoveLine(Guid transactionId, Guid lineId, CancellationToken ct);
    Task Complete(Guid id, CompleteTransactionRequest request, CancellationToken ct);
    Task<bool> Void(Guid id, VoidTransactionRequest request, CancellationToken ct);
    Task<bool> DeleteTransaction(Guid id, CancellationToken ct);
}
