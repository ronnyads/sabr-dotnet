using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Phub.Application.Services;

namespace Phub.Infrastructure.Services;

public sealed class FinancialSyncWorker : BackgroundService
{
    private const int ParallelTenantLanes = 4;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FinancialSyncWorker> _logger;
    private readonly string _workerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private DateTimeOffset _nextHistoryRepairAt;

    public FinancialSyncWorker(IServiceScopeFactory scopeFactory, ILogger<FinancialSyncWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        // Let API startup, migrations and high-priority incremental jobs settle
        // before scanning every existing seller for annual-history repairs.
        _nextHistoryRepairAt = DateTimeOffset.UtcNow.AddMinutes(2);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var repairLoop = RunHistoryRepairLoopAsync(stoppingToken);
        var lanes = Enumerable.Range(1, ParallelTenantLanes)
            .Select(lane => RunProcessingLaneAsync(lane, stoppingToken));
        await Task.WhenAll(lanes.Append(repairLoop));
    }

    private async Task RunHistoryRepairLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= _nextHistoryRepairAt)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<FinancialSyncJobService>();
                    var sellers = await service.EnsureExistingSellerHistoryAsync(stoppingToken);
                    _nextHistoryRepairAt = DateTimeOffset.UtcNow.AddHours(6);
                    _logger.LogInformation("Mercado Livre history coverage ensured for {SellerCount} sellers", sellers);
                }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Financial sync worker cycle failed");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    private async Task RunProcessingLaneAsync(int lane, CancellationToken stoppingToken)
    {
        var laneWorkerId = $"{_workerId}:lane-{lane}";
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<FinancialSyncJobService>();
                var worked = await service.ProcessNextAsync(laneWorkerId, stoppingToken);
                if (!worked) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Financial sync worker lane {Lane} failed", lane);
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }
}
