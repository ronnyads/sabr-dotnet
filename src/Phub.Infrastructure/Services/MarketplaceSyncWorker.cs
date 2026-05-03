using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Phub.Application.Services;

namespace Phub.Infrastructure.Services;

public sealed class MarketplaceSyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MarketplaceSyncWorker> _logger;

    public MarketplaceSyncWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MarketplaceSyncWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            await RunCycleAsync(stoppingToken);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunCycleAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            timer.Dispose();
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var mlSyncService = scope.ServiceProvider.GetRequiredService<MercadoLivreSyncService>();
            await mlSyncService.SyncAllConnectionsAsync(cancellationToken: cancellationToken);
            await mlSyncService.ExpireReservationsAsync(cancellationToken);

            var shopifySyncService = scope.ServiceProvider.GetRequiredService<ShopifyOAuthService>();
            await shopifySyncService.SyncAllConnectionsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MarketplaceSyncWorker cycle failed");
        }
    }
}
