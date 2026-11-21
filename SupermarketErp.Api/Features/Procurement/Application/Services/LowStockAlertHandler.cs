using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Procurement;

/// <summary>
/// Raises a low-stock alert when inventory reports a product at or below its reorder point. There is at most one
/// open alert per product, so repeated events (more sales, or a retry) change nothing.
/// </summary>
public sealed class LowStockAlertHandler(IProcurementRepository repository) : OutboxHandler<Guid>("LowStockThresholdReached")
{
    protected override async Task Handle(Guid productId, CancellationToken ct)
    {
        if (await repository.HasOpenAlert(productId, ct)) return;
        repository.Add(LowStockAlertEntity.Raise(productId, DateTimeOffset.UtcNow));
    }
}
