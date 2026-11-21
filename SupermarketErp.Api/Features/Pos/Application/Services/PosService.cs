using SupermarketErp.Api.Features.Inventory;
using SupermarketErp.Api.Features.Pricing;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Pos;

/// <summary>
/// POS use cases. This class orchestrates: it loads aggregates, calls the other modules' contracts (pricing,
/// inventory), lets the domain entities enforce their own rules, and publishes events, all in one unit of work.
/// State rules live in the domain entities (PosDomain.cs); persistence in <see cref="IPosRepository"/>.
/// </summary>
public sealed class PosService(
    IPosRepository repository,
    IUnitOfWork unitOfWork,
    IPricingCalculator pricing,
    IInventoryStock inventory,
    IEventOutbox outbox) : IPosService
{
    // ---- tills and sessions ----------------------------------------------------------------

    public async Task<IReadOnlyList<TillView>> ListTills(CancellationToken ct)
    {
        var tills = await repository.ListTills(ct);
        var sessions = await repository.ListSessions(ct);
        return tills.Select(till => PosViews.Till(
            till, sessions.Where(x => x.TillId == till.Id).OrderByDescending(x => x.OpenedAt).FirstOrDefault())).ToList();
    }

    public async Task<TillView?> GetTill(Guid id, CancellationToken ct)
    {
        var till = await repository.FindTill(id, ct);
        return till is null ? null : PosViews.Till(till, await repository.FindLatestSession(id, ct));
    }

    public async Task<Guid> CreateTill(CreateTillRequest request, CancellationToken ct)
    {
        var till = TillEntity.Create(request.TillNumber);
        await unitOfWork.InTransaction(() => { repository.Add(till); return Task.CompletedTask; }, ct);
        return till.Id;
    }

    public async Task<Guid> OpenSession(OpenSessionRequest request, CancellationToken ct)
    {
        var session = CashSessionEntity.Open(request.TillId, request.CashierName, request.OpeningFloat, DateTimeOffset.UtcNow);
        return await unitOfWork.InTransaction(async () =>
        {
            var till = await repository.FindTill(request.TillId, ct)
                ?? throw new DomainValidationException($"Till not found: {request.TillId}");
            if (await repository.FindOpenSession(request.TillId, ct) is not null)
                throw new ConflictException("Till already has an open cash session.");
            repository.Add(session);
            till.MarkOpen();
            return session.Id;
        }, ct);
    }

    public async Task<SessionView> GetSession(Guid id, CancellationToken ct) =>
        PosViews.Session(await repository.FindSession(id, ct) ?? throw SessionNotFound(id));

    public async Task<SessionView?> GetActiveSession(Guid tillId, CancellationToken ct) =>
        await repository.FindOpenSession(tillId, ct) is { } session ? PosViews.Session(session) : null;

    public Task<bool> CloseSession(Guid id, CloseSessionRequest request, CancellationToken ct) =>
        unitOfWork.InTransaction(async () =>
        {
            var session = await repository.FindSessionForUpdate(id, ct) ?? throw SessionNotFound(id);
            var expectedCash = session.Close(request.CountedCash, DateTimeOffset.UtcNow);
            var till = await repository.FindTill(session.TillId, ct)
                ?? throw new ConflictException($"Till not found: {session.TillId}");
            till.MarkClosed();
            outbox.Publish("CashSessionClosed", new CashSessionClosedEvent(id, expectedCash, request.CountedCash));
            return true;
        }, ct);

    // ---- tickets ---------------------------------------------------------------------------

    public Task<Guid> StartTransaction(StartTransactionRequest request, CancellationToken ct) =>
        unitOfWork.InTransaction(async () =>
        {
            var session = await repository.FindSession(request.CashSessionId, ct);
            var ticket = PosTransactionEntity.Start(session, DateTimeOffset.UtcNow);
            repository.Add(ticket);
            return ticket.Id;
        }, ct);

    public async Task<IReadOnlyList<TransactionView>> ListTransactions(CancellationToken ct) =>
        (await repository.ListTickets(ct)).Select(PosViews.Transaction).ToList();

    public async Task<TransactionView> GetTransaction(Guid id, CancellationToken ct) =>
        PosViews.Transaction(await repository.FindTicket(id, ct) ?? throw TicketNotFound(id));

    public Task<AddLineResponse> AddLine(Guid transactionId, AddLineRequest request, CancellationToken ct)
    {
        var (quantity, unit) = TransactionLineEntity.NormalizeQuantity(request.QuantityValue, request.UnitType);
        return unitOfWork.InTransaction(async () =>
        {
            var ticket = await repository.FindTicket(transactionId, ct)
                ?? throw new DomainValidationException("Ticket not found or is not in progress.");
            ticket.RequireAcceptsNewLines();
            await RequireOpenSession(ticket.CashSessionId, ct);

            var price = await pricing.CalculateLine(request.ProductId, quantity, unit, ct);
            var line = ticket.AddLine(request.ProductId, quantity, unit, price.Gross, price.Vat);
            repository.Add(line);
            return PosViews.AddedLine(price, line);
        }, ct);
    }

    public Task<bool> RemoveLine(Guid transactionId, Guid lineId, CancellationToken ct) =>
        unitOfWork.InTransaction(async () =>
        {
            var ticket = await repository.FindTicket(transactionId, ct) ?? throw TicketNotFound(transactionId);
            var removed = ticket.RemoveLine(lineId);
            if (removed is null) return false;
            repository.Remove(removed);
            return true;
        }, ct);

    /// <summary>
    /// Takes payment: checks the tender, sells every line out of inventory (first-expired-first-out), completes
    /// the ticket, books cash into the drawer and publishes <c>SaleCompleted</c>. All or nothing: if any line is
    /// out of stock, the whole sale rolls back.
    /// </summary>
    public Task Complete(Guid id, CompleteTransactionRequest request, CancellationToken ct)
    {
        var method = PaymentMethods.Parse(request.PaymentMethod);
        return unitOfWork.InTransaction(async () =>
        {
            var ticket = await repository.FindTicket(id, ct)
                ?? throw new ConflictException("Only in-progress tickets can be completed.");
            ticket.RequireCanComplete();
            var session = await RequireOpenSession(ticket.CashSessionId, ct, forUpdate: true);
            ticket.RequireTenderCoversTotal(method, request.AmountReceived);

            decimal costOfGoodsSold = 0;
            foreach (var line in ticket.Lines)
                costOfGoodsSold += await inventory.Sell(line.ProductId, line.Quantity, line.UnitName, ct);

            ticket.Complete(method, DateTimeOffset.UtcNow);
            if (method == PaymentMethods.Cash) session.RecordCashTakings(ticket.Total);

            outbox.Publish("SaleCompleted", new SaleCompletedEvent(
                id, ticket.CashSessionId, method, ticket.NetTotal, ticket.VatTotal, ticket.Total, costOfGoodsSold));
        }, ct);
    }

    public Task<bool> Void(Guid id, VoidTransactionRequest request, CancellationToken ct) =>
        unitOfWork.InTransaction(async () =>
        {
            var ticket = await repository.FindTicket(id, ct) ?? throw TicketNotFound(id);
            ticket.RequireInProgress();
            await RequireOpenSession(ticket.CashSessionId, ct);
            ticket.Void();
            return true;
        }, ct);

    public Task<bool> DeleteTransaction(Guid id, CancellationToken ct) =>
        unitOfWork.InTransaction(async () =>
        {
            var ticket = await repository.FindTicket(id, ct) ?? throw TicketNotFound(id);
            ticket.RequireInProgress();
            await RequireOpenSession(ticket.CashSessionId, ct);
            repository.Remove(ticket); // its lines are loaded with it, so they are deleted too
            return true;
        }, ct);

    // ---- helpers ---------------------------------------------------------------------------

    private async Task<CashSessionEntity> RequireOpenSession(Guid id, CancellationToken ct, bool forUpdate = false)
    {
        var session = await (forUpdate ? repository.FindSessionForUpdate(id, ct) : repository.FindSession(id, ct))
            ?? throw SessionNotFound(id);
        session.RequireOpen();
        return session;
    }

    private static DomainValidationException SessionNotFound(Guid id) => new($"Cash session not found: {id}");
    private static DomainValidationException TicketNotFound(Guid id) => new($"Ticket not found: {id}");
}
