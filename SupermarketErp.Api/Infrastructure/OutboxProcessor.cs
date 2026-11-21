using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SupermarketErp.Api.Infrastructure;

/// <summary>
/// Delivers outbox events at least once, in order, to the module handlers registered for their type
/// (<see cref="IOutboxHandler"/>). A failing event is retried with exponential backoff (2s, 4s, 8s, ...) without
/// holding up the events behind it, and is dead-lettered (<c>failed_at</c> set) after <see cref="MaxAttempts"/>
/// failures, or at once when it can never succeed (unknown type, bad payload). Handlers must be idempotent.
/// This class only moves events along: what an event means lives in the modules.
/// </summary>
public sealed class OutboxProcessor(IServiceScopeFactory scopes, ILogger<OutboxProcessor> logger) : BackgroundService
{
    public const int MaxAttempts = 5;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(1);

    /// <summary>An event that can never be handled, so retrying it is pointless.</summary>
    private sealed class PermanentOutboxException(string message) : Exception(message);

    private sealed record Claimed(Guid Id, string Type, int Attempts);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessNext(stoppingToken);
                if (!processed) await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Outbox processor loop failed");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    /// <summary>Handles the oldest due event. Returns false when there is nothing to do.</summary>
    private async Task<bool> ProcessNext(CancellationToken ct)
    {
        Claimed? claimed = null;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BusinessDbContext>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var handlers = scope.ServiceProvider.GetServices<IOutboxHandler>().ToDictionary(x => x.EventType);

            // READPAST needs read committed; the lock keeps a second processor from taking the same event.
            return await unitOfWork.InTransaction(async () =>
            {
                var now = DateTimeOffset.UtcNow;
                var row = (await db.DotnetOutbox.FromSqlInterpolated($"""
                    SELECT TOP (1) * FROM platform.dotnet_outbox WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE processed_at IS NULL AND failed_at IS NULL
                      AND (next_attempt_at IS NULL OR next_attempt_at <= {now})
                    ORDER BY occurred_at, id
                    """).ToListAsync(ct)).SingleOrDefault();
                if (row is null) return false;

                claimed = new Claimed(row.Id, row.EventType, row.Attempts);
                if (!handlers.TryGetValue(row.EventType, out var handler))
                    throw new PermanentOutboxException($"Unknown outbox event type '{row.EventType}'.");
                await handler.Handle(row.Payload, ct);

                row.ProcessedAt = DateTimeOffset.UtcNow;
                row.LastError = null;
                return true;
            }, ct, IsolationLevel.ReadCommitted);
        }
        catch (Exception exception) when (claimed is not null && !ct.IsCancellationRequested)
        {
            // The transaction was rolled back; the failure is recorded separately so it survives.
            await RecordFailure(claimed, exception, ct);
            return true;
        }
    }

    /// <summary>
    /// Records the failure and schedules the retry. The row is deferred (or dead-lettered), so the next poll
    /// moves on to the events behind it instead of hammering the failing one.
    /// </summary>
    private async Task RecordFailure(Claimed claimed, Exception exception, CancellationToken ct)
    {
        var permanent = exception is PermanentOutboxException or JsonException;
        var failedAttempts = claimed.Attempts + 1;
        var deadLetter = permanent || failedAttempts >= MaxAttempts;
        var now = DateTimeOffset.UtcNow;
        var backoff = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, failedAttempts), MaxBackoff.TotalSeconds));

        if (deadLetter)
            logger.LogCritical(exception, "Outbox event {OutboxId} ({EventType}) dead-lettered after {Attempts} attempt(s); " +
                "it will not be retried until its failed_at is cleared", claimed.Id, claimed.Type, failedAttempts);
        else
            logger.LogError(exception, "Outbox event {OutboxId} ({EventType}) failed (attempt {Attempts}/{Max}); retrying in {Backoff}s",
                claimed.Id, claimed.Type, failedAttempts, MaxAttempts, backoff.TotalSeconds);

        var error = exception.Message.Length > 4000 ? exception.Message[..4000] : exception.Message;
        DateTimeOffset? failedAt = deadLetter ? now : null;
        DateTimeOffset? nextAttempt = deadLetter ? null : now + backoff;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BusinessDbContext>();
        await db.DotnetOutbox.Where(x => x.Id == claimed.Id).ExecuteUpdateAsync(set => set
            .SetProperty(x => x.Attempts, failedAttempts)
            .SetProperty(x => x.LastError, error)
            .SetProperty(x => x.FailedAt, failedAt)
            .SetProperty(x => x.NextAttemptAt, nextAttempt), ct);
    }
}
