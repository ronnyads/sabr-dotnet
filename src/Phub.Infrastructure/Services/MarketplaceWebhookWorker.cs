using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Phub.Application.Services;

namespace Phub.Infrastructure.Services;

public sealed class MarketplaceWebhookWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MarketplaceWebhookWorker> _logger;

    public MarketplaceWebhookWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MarketplaceWebhookWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
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
            var webhookService = scope.ServiceProvider.GetRequiredService<MercadoLivreWebhookService>();
            await webhookService.ProcessPendingEventsAsync(100, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MarketplaceWebhookWorker cycle failed");
        }
    }
}
