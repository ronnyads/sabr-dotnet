using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Phub.Application.Services;

namespace Phub.Infrastructure.Services;

public sealed class FinancialSyncWorker : BackgroundService
{
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
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<FinancialSyncJobService>();
                if (DateTimeOffset.UtcNow >= _nextHistoryRepairAt)
                {
                    var sellers = await service.EnsureExistingSellerHistoryAsync(stoppingToken);
                    _nextHistoryRepairAt = DateTimeOffset.UtcNow.AddHours(6);
                    _logger.LogInformation("Mercado Livre history coverage ensured for {SellerCount} sellers", sellers);
                }
                var worked = await service.ProcessNextAsync(_workerId, stoppingToken);
                if (!worked) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
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
}
