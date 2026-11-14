namespace SupermarketErp.Api.Features.Pos;

public sealed class PosService(IPosRepository repository) : IPosService
{
    public Task<IReadOnlyList<TillView>> ListTills(CancellationToken ct) =>
        repository.ListTills(ct);

    public Task<TillView?> GetTill(Guid id, CancellationToken ct) =>
        repository.GetTill(id, ct);

    public Task<Guid> CreateTill(CreateTillRequest request, CancellationToken ct) =>
        repository.CreateTill(request, ct);

    public Task<Guid> OpenSession(OpenSessionRequest request, CancellationToken ct) =>
        repository.OpenSession(request, ct);

    public Task<SessionView> GetSession(Guid id, CancellationToken ct) =>
        repository.GetSession(id, ct);

    public Task<SessionView?> GetActiveSession(Guid tillId, CancellationToken ct) =>
        repository.GetActiveSession(tillId, ct);

    public Task<bool> CloseSession(Guid id, CloseSessionRequest request, CancellationToken ct) =>
        repository.CloseSession(id, request, ct);

    public Task<Guid> StartTransaction(StartTransactionRequest request, CancellationToken ct) =>
        repository.StartTransaction(request, ct);

    public Task<IReadOnlyList<TransactionView>> ListTransactions(CancellationToken ct) =>
        repository.ListTransactions(ct);

    public Task<TransactionView> GetTransaction(Guid id, CancellationToken ct) =>
        repository.GetTransaction(id, ct);

    public Task<AddLineResponse> AddLine(
        Guid transactionId,
        AddLineRequest request,
        CancellationToken ct) =>
        repository.AddLine(transactionId, request, ct);

    public Task<bool> RemoveLine(Guid transactionId, Guid lineId, CancellationToken ct) =>
        repository.RemoveLine(transactionId, lineId, ct);

    public Task Complete(Guid id, CompleteTransactionRequest request, CancellationToken ct) =>
        repository.Complete(id, request, ct);

    public Task<bool> Void(Guid id, VoidTransactionRequest request, CancellationToken ct) =>
        repository.Void(id, request, ct);

    public Task<bool> DeleteTransaction(Guid id, CancellationToken ct) =>
        repository.DeleteTransaction(id, ct);
}
