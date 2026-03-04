using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sabr.Application.Options;
using Sabr.Application.Services;

namespace Sabr.Infrastructure.Services;

public sealed class ProductVariantBackfillWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProductVariantBackfillWorker> _logger;
    private readonly ProductVariantBackfillOptions _options;

    public ProductVariantBackfillWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<ProductVariantBackfillOptions> options,
        ILogger<ProductVariantBackfillWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("ProductVariantBackfillWorker disabled.");
            return;
        }

        var intervalMinutes = Math.Max(1, _options.IntervalMinutes);
        var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));
        try
        {
            if (_options.RunOnStartup)
            {
                await RunCycleAsync(stoppingToken);
            }

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
            var service = scope.ServiceProvider.GetRequiredService<ProductVariantBackfillService>();
            var result = await service.RunOnceAsync(Math.Max(1, _options.BatchSize), cancellationToken);
            _logger.LogInformation(
                "ProductVariantBackfillWorker cycle: processed={Processed}, created={Created}, skippedMissingProduct={Skipped}, alreadyExists={AlreadyExists}, errors={Errors}",
                result.Processed,
                result.Created,
                result.SkippedMissingProduct,
                result.AlreadyExists,
                result.Errors);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ProductVariantBackfillWorker cycle failed");
        }
    }
}

