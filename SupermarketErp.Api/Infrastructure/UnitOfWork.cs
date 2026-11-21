using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SupermarketErp.Api.Infrastructure;

/// <summary>
/// The transaction boundary for application services, so they do not touch the database context themselves.
/// Everything done inside <see cref="InTransaction{T}"/> (aggregate changes, stock movements by other modules,
/// outbox events) commits or rolls back together.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Runs the work in one transaction (serializable unless stated otherwise), saves pending changes, and commits.
    /// If SQL Server picks the transaction as a deadlock victim, the whole unit is run again from a clean state,
    /// so the work must load what it changes inside the delegate and be free of side effects outside the database.
    /// </summary>
    Task<T> InTransaction<T>(Func<Task<T>> work, CancellationToken ct, System.Data.IsolationLevel? isolation = null);

    Task InTransaction(Func<Task> work, CancellationToken ct, System.Data.IsolationLevel? isolation = null);
}

public sealed class UnitOfWork(BusinessDbContext db, ILogger<UnitOfWork> logger) : IUnitOfWork
{
    public const int MaxAttempts = 4;
    private const int DeadlockVictim = 1205;

    public async Task<T> InTransaction<T>(Func<Task<T>> work, CancellationToken ct, System.Data.IsolationLevel? isolation = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RunOnce(work, isolation ?? System.Data.IsolationLevel.Serializable, ct);
            }
            catch (Exception ex) when (IsDeadlock(ex) && attempt < MaxAttempts && !ct.IsCancellationRequested)
            {
                // The transaction was rolled back by SQL Server; forget everything the failed attempt tracked.
                db.ChangeTracker.Clear();
                var delay = TimeSpan.FromMilliseconds(Random.Shared.Next(20, 60) * attempt);
                logger.LogWarning("Deadlock victim on attempt {Attempt} of {Max}; the unit of work will be retried in {Delay} ms", attempt, MaxAttempts, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    public Task InTransaction(Func<Task> work, CancellationToken ct, System.Data.IsolationLevel? isolation = null) =>
        InTransaction<object?>(async () => { await work(); return null; }, ct, isolation);

    private async Task<T> RunOnce<T>(Func<Task<T>> work, System.Data.IsolationLevel isolation, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(isolation, ct);
        var result = await work();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    private static bool IsDeadlock(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is Microsoft.Data.SqlClient.SqlException sql && sql.Errors.Cast<Microsoft.Data.SqlClient.SqlError>().Any(x => x.Number == DeadlockVictim))
                return true;
        return false;
    }
}

/// <summary>
/// Publishes integration events through the transactional outbox. The event is stored with the caller's other
/// changes, so it exists if and only if the business change commits.
/// </summary>
public interface IEventOutbox
{
    void Publish<T>(string eventType, T payload);
}

public sealed class EventOutbox(BusinessDbContext db) : IEventOutbox
{
    public void Publish<T>(string eventType, T payload) =>
        db.DotnetOutbox.Add(new DotnetOutboxEntity
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            Payload = JsonSerializer.Serialize(payload),
            OccurredAt = DateTimeOffset.UtcNow
        });
}
