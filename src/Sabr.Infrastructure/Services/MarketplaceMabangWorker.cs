using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Phub.Application.Services;

namespace Phub.Infrastructure.Services;

public sealed class MarketplaceMabangWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MarketplaceMabangWorker> _logger;

    public MarketplaceMabangWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MarketplaceMabangWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
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
            var service = scope.ServiceProvider.GetRequiredService<MarketplaceMabangDispatchService>();
            await service.ProcessQueueAsync(50, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MarketplaceMabangWorker cycle failed");
        }
    }
}
