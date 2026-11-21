namespace SupermarketErp.Api.Features.Ecommerce;

/// <summary>Periodically expires unpaid online orders so their reserved stock goes back on the shelf.</summary>
public sealed class OnlineOrderExpiryWorker(IServiceScopeFactory scopeFactory, ILogger<OnlineOrderExpiryWorker> logger,
    IConfiguration configuration) : BackgroundService
{
    private readonly int _scanIntervalMs = int.TryParse(configuration["ECOMMERCE_EXPIRY_SCAN_INTERVAL_MS"], out var interval)
        && interval > 0 ? interval : configuration["ECOMMERCE_EXPIRY_SCAN_INTERVAL_MS"] is null ? 60_000
        : throw new DomainValidationException("ECOMMERCE_EXPIRY_SCAN_INTERVAL_MS must be positive.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IStorefrontOrderService>();
                await service.ExpireDueOrders(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Failed to expire online orders"); }
            await Task.Delay(TimeSpan.FromMilliseconds(_scanIntervalMs), stoppingToken);
        }
    }
}





