using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Phub.Application.Services;

namespace Phub.Infrastructure.Services;

public sealed class SentinelReconciliationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SentinelReconciliationWorker> _logger;

    public SentinelReconciliationWorker(IServiceScopeFactory scopeFactory, ILogger<SentinelReconciliationWorker> logger)
        => (_scopeFactory, _logger) = (scopeFactory, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<SentinelReconciliationService>();
                    var count = await service.EnqueueDueAsync(stoppingToken);
                    var sentinel = scope.ServiceProvider.GetRequiredService<SentinelService>();
                    await sentinel.EmitLevelChangesAsync(stoppingToken);
                    if (count > 0) _logger.LogInformation("SENTINEL queued {Count} shipment reconciliations", count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SENTINEL reconciliation cycle failed");
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
