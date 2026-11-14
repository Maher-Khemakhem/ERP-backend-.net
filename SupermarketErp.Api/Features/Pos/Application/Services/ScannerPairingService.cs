using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Pos;

public sealed class ScannerPairingService(
    BusinessDbContext db,
    IPosService pos,
    ScannerPairingStore store)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public async Task<ScannerPairingView> Create(Guid transactionId, CancellationToken ct)
    {
        var transaction = await pos.GetTransaction(transactionId, ct)
            ?? throw new ArgumentException($"Ticket not found: {transactionId}");
        if (transaction.Status != "IN_PROGRESS")
            throw new InvalidOperationException("Only an in-progress ticket can use the phone scanner.");

        var now = DateTimeOffset.UtcNow;
        store.RemoveExpiredOrForTransaction(now, transactionId);

        var id = Guid.NewGuid();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var expires = now.Add(Lifetime);
        store.Add(new ScannerPairingStore.Pairing(id, token, transactionId, expires));
        return new ScannerPairingView(id, token, JavaInstantFormat.Format(expires));
    }

    public async Task<ScannerScanResult> Scan(
        Guid sessionId,
        string? token,
        string rawBarcode,
        CancellationToken ct)
    {
        var pairing = Require(sessionId, token);
        var transaction = await pos.GetTransaction(pairing.TransactionId, ct)
            ?? throw new ArgumentException("Scanner ticket no longer exists.");
        if (transaction.Status != "IN_PROGRESS")
            throw new InvalidOperationException("Only an in-progress ticket can use the phone scanner.");

        var barcode = rawBarcode?.Trim() ?? "";
        var product = await db.Products.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Sku.ToLower() == barcode.ToLower(), ct)
            ?? throw new ArgumentException($"No product found for barcode: {barcode}");

        var quantity = product.UnitType == "WEIGHT" ? 0.1m : 1m;
        var line = await pos.AddLine(
            pairing.TransactionId,
            new AddLineRequest(product.Id, quantity, product.UnitType),
            ct);

        return new ScannerScanResult(
            barcode,
            product.Id,
            product.Sku,
            product.Name,
            product.UnitType,
            product.UnitType == "WEIGHT" ? "0.100 kg" : "1 unit(s)",
            line.LinePrice,
            line.NetPrice,
            line.VatAmount);
    }

    public void Close(Guid sessionId, string? token)
    {
        _ = Require(sessionId, token);
        store.Remove(sessionId);
    }

    private ScannerPairingStore.Pairing Require(Guid sessionId, string? token)
    {
        if (!store.TryGet(sessionId, out var pairing) || pairing is null ||
            pairing.ExpiresAt < DateTimeOffset.UtcNow)
        {
            store.Remove(sessionId);
            throw new ArgumentException("Scanner pairing has expired or does not exist.");
        }

        if (token is null || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(token),
                System.Text.Encoding.UTF8.GetBytes(pairing.Token)))
            throw new ArgumentException("Invalid scanner pairing token.");

        return pairing;
    }
}
