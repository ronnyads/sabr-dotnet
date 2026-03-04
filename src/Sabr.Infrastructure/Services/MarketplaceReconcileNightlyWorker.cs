using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sabr.Application.Options;
using Sabr.Application.Services;

namespace Sabr.Infrastructure.Services;

public sealed class MarketplaceReconcileNightlyWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MarketplaceReconcileNightlyWorker> _logger;
    private readonly MercadoLivreOptions _options;

    public MarketplaceReconcileNightlyWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<MercadoLivreOptions> options,
        ILogger<MarketplaceReconcileNightlyWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timer = new PeriodicTimer(TimeSpan.FromHours(24));
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
            if (!_options.Features.Reconcile)
            {
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var syncService = scope.ServiceProvider.GetRequiredService<MercadoLivreSyncService>();
            await syncService.SyncAllConnectionsAsync(
                Math.Max(1, _options.NightlyReconcileLookbackDays),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MarketplaceReconcileNightlyWorker cycle failed");
        }
    }
}
