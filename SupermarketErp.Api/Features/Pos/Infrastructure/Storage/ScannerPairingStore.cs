using System.Collections.Concurrent;

namespace SupermarketErp.Api.Features.Pos;

public sealed class ScannerPairingStore
{
    private readonly ConcurrentDictionary<Guid, Pairing> _sessions = new();

    public void RemoveExpiredOrForTransaction(DateTimeOffset now, Guid transactionId)
    {
        foreach (var existing in _sessions.Where(pair =>
                     pair.Value.ExpiresAt < now || pair.Value.TransactionId == transactionId))
            _sessions.TryRemove(existing.Key, out _);
    }

    public void Add(Pairing pairing) => _sessions[pairing.Id] = pairing;

    public bool TryGet(Guid sessionId, out Pairing? pairing) =>
        _sessions.TryGetValue(sessionId, out pairing);

    public void Remove(Guid sessionId) => _sessions.TryRemove(sessionId, out _);

    public sealed record Pairing(
        Guid Id,
        string Token,
        Guid TransactionId,
        DateTimeOffset ExpiresAt);
}
