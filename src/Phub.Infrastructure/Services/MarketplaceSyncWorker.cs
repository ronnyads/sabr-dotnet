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
        using var scope = _scopeFactory.CreateScope();
        var mlSyncService = scope.ServiceProvider.GetRequiredService<MercadoLivreSyncService>();
        var shopeeSyncService = scope.ServiceProvider.GetRequiredService<ShopeeOAuthService>();
        var shopifySyncService = scope.ServiceProvider.GetRequiredService<ShopifyOAuthService>();
        var tikTokShopSyncService = scope.ServiceProvider.GetRequiredService<TikTokShopSyncService>();
        var tinyIntegrationService = scope.ServiceProvider.GetRequiredService<TinyIntegrationService>();

        await RunProviderSyncAsync(
            "MercadoLivre",
            async () =>
            {
                await mlSyncService.SyncAllConnectionsAsync(cancellationToken: cancellationToken);
                await mlSyncService.ExpireReservationsAsync(cancellationToken);
            });

        await RunProviderSyncAsync(
            "Shopee",
            () => shopeeSyncService.SyncAllConnectionsAsync(cancellationToken));

        await RunProviderSyncAsync(
            "Shopify",
            () => shopifySyncService.SyncAllConnectionsAsync(cancellationToken));

        await RunProviderSyncAsync(
            "TikTokShop",
            () => tikTokShopSyncService.SyncAllConnectionsAsync(cancellationToken));

        await RunProviderSyncAsync(
            "TinyErp",
            () => tinyIntegrationService.SyncAllConnectionsAsync(cancellationToken));
    }

    private async Task RunProviderSyncAsync(string provider, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MarketplaceSyncWorker provider cycle failed. provider={Provider}", provider);
        }
    }
}
