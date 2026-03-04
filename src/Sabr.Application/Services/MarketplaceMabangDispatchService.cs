using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Options;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;

namespace Sabr.Application.Services;

public sealed class MarketplaceMabangDispatchService
{
    private readonly IAppDbContext _dbContext;
    private readonly IMabangApiClient _mabangApiClient;
    private readonly MercadoLivreOptions _options;
    private readonly ILogger<MarketplaceMabangDispatchService> _logger;

    public MarketplaceMabangDispatchService(
        IAppDbContext dbContext,
        IMabangApiClient mabangApiClient,
        IOptions<MercadoLivreOptions> options,
        ILogger<MarketplaceMabangDispatchService> logger)
    {
        _dbContext = dbContext;
        _mabangApiClient = mabangApiClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnqueueLabelDispatchAsync(
        Domain.Entities.MarketplaceShipment shipment,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Features.Mabang
            || string.IsNullOrWhiteSpace(shipment.LabelSha256)
            || shipment.LabelContentBytes == null
            || shipment.LabelContentBytes.Length == 0)
        {
            return;
        }

        var dedupeKey = $"mabang:label:{shipment.Provider}:{shipment.TenantId}:{shipment.ClientId}:{shipment.SellerId}:{shipment.ShipmentId}:{shipment.LabelSha256}";
        var exists = await _dbContext.MarketplaceEventLogs.AsNoTracking().AnyAsync(
            item => item.DedupeKey == dedupeKey,
            cancellationToken);
        if (exists)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _dbContext.MarketplaceEventLogs.Add(new MarketplaceEventLog
        {
            Id = Guid.NewGuid(),
            TenantId = shipment.TenantId,
            ClientId = shipment.ClientId,
            Provider = shipment.Provider,
            SellerId = shipment.SellerId,
            Topic = MarketplaceEventTopics.MabangLabelDispatch,
            ResourceId = shipment.ShipmentId,
            DedupeKey = dedupeKey,
            Status = MarketplaceEventStatuses.Pending,
            Attempts = 0,
            PayloadJson = JsonSerializer.Serialize(new
            {
                shipmentId = shipment.ShipmentId,
                sha256 = shipment.LabelSha256
            }),
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    public async Task<int> ProcessQueueAsync(int maxBatch = 50, CancellationToken cancellationToken = default)
    {
        if (!_options.Features.Mabang)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var batchSize = Math.Min(200, Math.Max(1, maxBatch));
        var candidates = await _dbContext.MarketplaceEventLogs
            .Where(item => item.Topic == MarketplaceEventTopics.MabangLabelDispatch
                           && (item.Status == MarketplaceEventStatuses.Pending
                               || item.Status == MarketplaceEventStatuses.Failed))
            .OrderBy(item => item.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var processed = 0;
        foreach (var evt in candidates)
        {
            if (!IsDueForRetry(evt, now))
            {
                continue;
            }

            evt.Attempts += 1;
            try
            {
                var shipment = await _dbContext.MarketplaceShipments.FirstOrDefaultAsync(
                    item => item.TenantId == evt.TenantId
                            && item.ClientId == evt.ClientId
                            && item.Provider == evt.Provider
                            && item.ShipmentId == evt.ResourceId,
                    cancellationToken);
                if (shipment == null || shipment.LabelContentBytes == null || shipment.LabelContentBytes.Length == 0)
                {
                    evt.Status = MarketplaceEventStatuses.Failed;
                    evt.LastErrorAt = now;
                    evt.LastError = "MABANG_LABEL_NOT_AVAILABLE";
                    evt.UpdatedAt = now;
                    processed += 1;
                    continue;
                }

                var request = new MabangLabelDispatchRequest
                {
                    TenantId = evt.TenantId,
                    ClientId = evt.ClientId,
                    SellerId = MercadoLivreSellerIdParser.ToApiString(evt.SellerId),
                    ShipmentId = shipment.ShipmentId,
                    ContentType = string.IsNullOrWhiteSpace(shipment.LabelContentType) ? "application/pdf" : shipment.LabelContentType,
                    LabelSha256 = shipment.LabelSha256 ?? string.Empty,
                    LabelBase64 = Convert.ToBase64String(shipment.LabelContentBytes)
                };
                await _mabangApiClient.SendLabelAsync(request, cancellationToken);

                evt.Status = MarketplaceEventStatuses.Processed;
                evt.ProcessedAt = now;
                evt.LastError = null;
                evt.LastErrorAt = null;
                evt.UpdatedAt = now;
                processed += 1;
            }
            catch (Exception ex)
            {
                evt.Status = MarketplaceEventStatuses.Failed;
                evt.LastErrorAt = now;
                evt.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                evt.UpdatedAt = now;
                _logger.LogWarning(
                    ex,
                    "Mabang dispatch failed tenant={TenantId} client={ClientId} shipment={ShipmentId} attempt={Attempt}",
                    evt.TenantId,
                    evt.ClientId,
                    evt.ResourceId,
                    evt.Attempts);
                processed += 1;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return processed;
    }

    private bool IsDueForRetry(MarketplaceEventLog evt, DateTimeOffset now)
    {
        if (evt.Status == MarketplaceEventStatuses.Pending)
        {
            return true;
        }

        if (evt.Status != MarketplaceEventStatuses.Failed || !evt.LastErrorAt.HasValue)
        {
            return true;
        }

        var baseDelay = Math.Max(1, _options.Resilience.RetryBaseDelayMs);
        var retryAttempt = Math.Max(1, evt.Attempts);
        var delayMs = Math.Min(60000, (int)(baseDelay * Math.Pow(2, retryAttempt - 1)));
        return evt.LastErrorAt.Value.AddMilliseconds(delayMs) <= now;
    }
}
