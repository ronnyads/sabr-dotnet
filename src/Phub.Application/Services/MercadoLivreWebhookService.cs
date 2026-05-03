using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Options;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class MercadoLivreWebhookService
{
    private const int MaxWebhookAttempts = 10;
    private const int MaxRetryDelayMs = 60_000;
    private readonly IAppDbContext _dbContext;
    private readonly MercadoLivreSyncService _syncService;
    private readonly MercadoLivreOAuthService _oauthService;
    private readonly IMercadoLivreApiClient _mercadoLivreApiClient;
    private readonly MarketplaceAuditLogService _auditLogService;
    private readonly MercadoLivreOptions _options;
    private readonly ILogger<MercadoLivreWebhookService> _logger;

    public MercadoLivreWebhookService(
        IAppDbContext dbContext,
        MercadoLivreSyncService syncService,
        MercadoLivreOAuthService oauthService,
        IMercadoLivreApiClient mercadoLivreApiClient,
        MarketplaceAuditLogService auditLogService,
        ILogger<MercadoLivreWebhookService> logger,
        IOptions<MercadoLivreOptions> options)
    {
        _dbContext = dbContext;
        _syncService = syncService;
        _oauthService = oauthService;
        _mercadoLivreApiClient = mercadoLivreApiClient;
        _auditLogService = auditLogService;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<ServiceResult<MercadoLivreWebhookIngestResult>> IngestAsync(
        MercadoLivreWebhookPayload payload,
        string? providedSecret,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Features.Webhook)
        {
            return ServiceResult<MercadoLivreWebhookIngestResult>.Failure(new[]
            {
                new ValidationError("feature", "ML_WEBHOOK_DISABLED")
            });
        }

        var topic = (payload.Topic ?? string.Empty).Trim();
        var resourceId = (payload.Resource ?? string.Empty).Trim();
        var rawSellerId = (payload.UserId ?? string.Empty).Trim();
        var notificationId = (payload.Id ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(resourceId) || string.IsNullOrWhiteSpace(rawSellerId))
        {
            return ServiceResult<MercadoLivreWebhookIngestResult>.Failure(new[]
            {
                new ValidationError("payload", "Invalid webhook payload")
            });
        }

        if (!MercadoLivreSellerIdParser.TryParseRequired(rawSellerId, out var sellerId))
        {
            return ServiceResult<MercadoLivreWebhookIngestResult>.Failure(new[]
            {
                new ValidationError("sellerId", "ML_WEBHOOK_SELLER_INVALID")
            });
        }

        if (!ValidateSecret(providedSecret))
        {
            return ServiceResult<MercadoLivreWebhookIngestResult>.Failure(new[]
            {
                new ValidationError("secret", "ML_WEBHOOK_VERIFICATION_FAILED")
            });
        }

        var connection = await _dbContext.TenantMarketplaceConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Provider == MarketplaceProvider.MercadoLivre
                                         && item.SellerId == sellerId,
                cancellationToken);
        if (connection == null)
        {
            return ServiceResult<MercadoLivreWebhookIngestResult>.Failure(new[]
            {
                new ValidationError("sellerId", "ML_WEBHOOK_UNKNOWN_SELLER")
            });
        }

        var dedupeKey = BuildDedupeKey(topic, resourceId, sellerId, notificationId);
        var existing = await _dbContext.MarketplaceEventLogs
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.DedupeKey == dedupeKey, cancellationToken);
        if (existing != null)
        {
            return ServiceResult<MercadoLivreWebhookIngestResult>.Success(new MercadoLivreWebhookIngestResult
            {
                Accepted = true,
                Duplicate = true,
                EventId = existing.Id.ToString("D")
            });
        }

        var now = DateTimeOffset.UtcNow;
        var eventLog = new MarketplaceEventLog
        {
            Id = Guid.NewGuid(),
            TenantId = connection.TenantId,
            ClientId = connection.ClientId,
            Provider = MarketplaceProvider.MercadoLivre,
            SellerId = sellerId,
            Topic = topic,
            ResourceId = resourceId,
            NotificationId = string.IsNullOrWhiteSpace(notificationId) ? null : notificationId,
            DedupeKey = dedupeKey,
            Status = MarketplaceEventStatuses.Pending,
            Attempts = 0,
            PayloadJson = JsonSerializer.Serialize(payload),
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.MarketplaceEventLogs.Add(eventLog);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            var duplicate = await _dbContext.MarketplaceEventLogs
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.DedupeKey == dedupeKey, cancellationToken);
            return ServiceResult<MercadoLivreWebhookIngestResult>.Success(new MercadoLivreWebhookIngestResult
            {
                Accepted = true,
                Duplicate = duplicate != null,
                EventId = duplicate?.Id.ToString("D")
            });
        }

        return ServiceResult<MercadoLivreWebhookIngestResult>.Success(new MercadoLivreWebhookIngestResult
        {
            Accepted = true,
            Duplicate = false,
            EventId = eventLog.Id.ToString("D")
        });
    }

    public async Task<int> ProcessPendingEventsAsync(int maxBatch = 100, CancellationToken cancellationToken = default)
    {
        if (!_options.Features.Webhook)
        {
            return 0;
        }

        var batchSize = Math.Min(500, Math.Max(1, maxBatch));
        var now = DateTimeOffset.UtcNow;
        var candidates = await _dbContext.MarketplaceEventLogs
            .AsNoTracking()
            .Where(item => (item.Status == MarketplaceEventStatuses.Pending || item.Status == MarketplaceEventStatuses.Failed)
                           && (item.Topic == MarketplaceEventTopics.WebhookOrders
                               || item.Topic == MarketplaceEventTopics.WebhookShipments
                               || item.Topic == MarketplaceEventTopics.WebhookPayments))
            .OrderBy(item => item.CreatedAt)
            .Take(batchSize * 2)
            .Select(item => new WebhookCandidate(item.Id, item.Status, item.Attempts, item.LastErrorAt))
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return 0;
        }

        var processed = 0;
        foreach (var candidate in candidates)
        {
            if (!IsDueForRetry(candidate, now))
            {
                continue;
            }

            if (!await ClaimForProcessingAsync(candidate.Id, cancellationToken))
            {
                continue;
            }

            var item = await _dbContext.MarketplaceEventLogs
                .FirstOrDefaultAsync(entry => entry.Id == candidate.Id, cancellationToken);
            if (item == null)
            {
                continue;
            }

            try
            {
                var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
                    conn => conn.TenantId == item.TenantId
                            && conn.ClientId == item.ClientId
                            && conn.Provider == item.Provider
                            && conn.SellerId == item.SellerId,
                    cancellationToken);
                if (connection == null)
                {
                    MarkFailed(item, "ML_WEBHOOK_UNKNOWN_SELLER");
                    processed += 1;
                    continue;
                }

                var accessToken = await _oauthService.GetValidAccessTokenAsync(connection, cancellationToken);
                var resourceValidation = await ValidateResourceOwnershipAsync(item, accessToken, cancellationToken);
                if (!resourceValidation.isValid)
                {
                    MarkFailed(item, resourceValidation.errorCode ?? "ML_WEBHOOK_RESOURCE_INVALID");
                    processed += 1;
                    continue;
                }

                if (resourceValidation.shipmentDetails != null)
                {
                    await UpsertShipmentProjectionAsync(item, resourceValidation.shipmentDetails, cancellationToken);
                }

                var syncResult = await _syncService.SyncNowAsync(
                    item.TenantId,
                    item.ClientId,
                    MercadoLivreSellerIdParser.ToApiString(item.SellerId),
                    cancellationToken);

                if (syncResult.Succeeded)
                {
                    item.Status = MarketplaceEventStatuses.Processed;
                    item.ProcessedAt = DateTimeOffset.UtcNow;
                    item.LastError = null;
                    item.LastErrorAt = null;
                    item.UpdatedAt = DateTimeOffset.UtcNow;
                }
                else
                {
                    MarkFailed(item, string.Join("; ", syncResult.Errors.Select(error => error.Message)));
                }
            }
            catch (Exception ex)
            {
                MarkFailed(item, ex.Message);
                _logger.LogWarning(
                    ex,
                    "Webhook processing failed for event {EventId} topic={Topic} tenant={TenantId} client={ClientId} attempt={Attempt}",
                    item.Id,
                    item.Topic,
                    item.TenantId,
                    item.ClientId,
                    item.Attempts);
            }

            processed += 1;
            if (processed >= batchSize)
            {
                break;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return processed;
    }

    private async Task<bool> ClaimForProcessingAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var claimedAt = DateTimeOffset.UtcNow;
        if ((_dbContext as DbContext)?.Database.IsRelational() != true)
        {
            var inMemoryItem = await _dbContext.MarketplaceEventLogs
                .FirstOrDefaultAsync(
                    item => item.Id == eventId
                            && (item.Status == MarketplaceEventStatuses.Pending || item.Status == MarketplaceEventStatuses.Failed),
                    cancellationToken);
            if (inMemoryItem == null)
            {
                return false;
            }

            inMemoryItem.Status = MarketplaceEventStatuses.Processing;
            inMemoryItem.Attempts += 1;
            inMemoryItem.UpdatedAt = claimedAt;
            return true;
        }

        var updatedRows = await _dbContext.MarketplaceEventLogs
            .Where(item => item.Id == eventId
                           && (item.Status == MarketplaceEventStatuses.Pending || item.Status == MarketplaceEventStatuses.Failed))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, MarketplaceEventStatuses.Processing)
                    .SetProperty(item => item.Attempts, item => item.Attempts + 1)
                    .SetProperty(item => item.UpdatedAt, claimedAt),
                cancellationToken);

        return updatedRows > 0;
    }

    private bool IsDueForRetry(WebhookCandidate candidate, DateTimeOffset now)
    {
        if (candidate.Status == MarketplaceEventStatuses.Pending)
        {
            return true;
        }

        if (candidate.Status != MarketplaceEventStatuses.Failed || !candidate.LastErrorAt.HasValue)
        {
            return false;
        }

        var baseDelayMs = Math.Max(1000, _options.Resilience.RetryBaseDelayMs);
        var retryAttempt = Math.Max(1, candidate.Attempts);
        var delayMs = Math.Min(MaxRetryDelayMs, (int)(baseDelayMs * Math.Pow(2, Math.Min(6, retryAttempt - 1))));
        return candidate.LastErrorAt.Value.AddMilliseconds(delayMs) <= now;
    }

    private void MarkFailed(MarketplaceEventLog item, string error)
    {
        var now = DateTimeOffset.UtcNow;
        item.LastErrorAt = now;
        item.LastError = TrimError(error);
        item.UpdatedAt = now;

        if (item.Attempts >= MaxWebhookAttempts)
        {
            item.Status = MarketplaceEventStatuses.DeadLetter;
            return;
        }

        item.Status = MarketplaceEventStatuses.Failed;
    }

    private static string TrimError(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "UNKNOWN_ERROR";
        }

        var normalized = value.Trim();
        return normalized.Length <= 1000 ? normalized : normalized[..1000];
    }

    private async Task<(bool isValid, string? errorCode, MercadoLivreShipmentDetails? shipmentDetails)> ValidateResourceOwnershipAsync(
        MarketplaceEventLog item,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var topic = item.Topic.Trim().ToLowerInvariant();
        if (topic.Contains("orders", StringComparison.Ordinal) || topic.Contains("payments", StringComparison.Ordinal))
        {
            var orderId = ExtractResourceId(item.ResourceId, "orders");
            if (string.IsNullOrWhiteSpace(orderId))
            {
                return (false, "ML_WEBHOOK_ORDER_RESOURCE_INVALID", null);
            }

            var order = await _mercadoLivreApiClient.GetOrderAsync(orderId, accessToken, cancellationToken);
            if (order == null)
            {
                return (false, "ML_WEBHOOK_ORDER_NOT_FOUND", null);
            }

            if (!SellerMatches(order.SellerId, item.SellerId))
            {
                return (false, "ML_WEBHOOK_RESOURCE_OWNER_MISMATCH", null);
            }

            return (true, null, null);
        }

        if (topic.Contains("shipments", StringComparison.Ordinal))
        {
            var shipmentId = ExtractResourceId(item.ResourceId, "shipments");
            if (string.IsNullOrWhiteSpace(shipmentId))
            {
                return (false, "ML_WEBHOOK_SHIPMENT_RESOURCE_INVALID", null);
            }

            var shipment = await _mercadoLivreApiClient.GetShipmentAsync(shipmentId, accessToken, cancellationToken);
            if (shipment == null)
            {
                return (false, "ML_WEBHOOK_SHIPMENT_NOT_FOUND", null);
            }

            if (!SellerMatches(shipment.SellerId, item.SellerId))
            {
                return (false, "ML_WEBHOOK_RESOURCE_OWNER_MISMATCH", null);
            }

            return (true, null, shipment);
        }

        return (false, "ML_WEBHOOK_TOPIC_NOT_SUPPORTED", null);
    }

    private async Task UpsertShipmentProjectionAsync(
        MarketplaceEventLog eventLog,
        MercadoLivreShipmentDetails shipmentDetails,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(shipmentDetails.ShipmentId))
        {
            return;
        }

        var shipment = await _dbContext.MarketplaceShipments.FirstOrDefaultAsync(
            item => item.TenantId == eventLog.TenantId
                    && item.ClientId == eventLog.ClientId
                    && item.Provider == eventLog.Provider
                    && item.ShipmentId == shipmentDetails.ShipmentId,
            cancellationToken);
        if (shipment == null)
        {
            shipment = new MarketplaceShipment
            {
                Id = Guid.NewGuid(),
                TenantId = eventLog.TenantId,
                ClientId = eventLog.ClientId,
                Provider = eventLog.Provider,
                SellerId = eventLog.SellerId,
                ShipmentId = shipmentDetails.ShipmentId
            };
            _dbContext.MarketplaceShipments.Add(shipment);
        }

        shipment.Status = shipmentDetails.Status;
        shipment.Substatus = shipmentDetails.Substatus;
        shipment.ShippingMode = shipmentDetails.ShippingMode;
        shipment.LogisticType = shipmentDetails.LogisticType;
        shipment.TrackingNumber = shipmentDetails.TrackingNumber;
        shipment.TrackingMethod = shipmentDetails.TrackingMethod;
        shipment.TrackingUrl = shipmentDetails.TrackingUrl;
        shipment.ShippedAt = shipmentDetails.ShippedAt;
        shipment.ShipByDeadlineAt = shipmentDetails.ShipByDeadlineAt;
        shipment.UpdatedAt = DateTimeOffset.UtcNow;

        var relatedOrders = await _dbContext.MarketplaceOrders
            .Where(item => item.TenantId == eventLog.TenantId
                           && item.ClientId == eventLog.ClientId
                           && item.Provider == eventLog.Provider
                           && item.SellerId == eventLog.SellerId
                           && item.ShipmentId == shipmentDetails.ShipmentId)
            .ToListAsync(cancellationToken);
        foreach (var order in relatedOrders)
        {
            order.ShippingMode = shipmentDetails.ShippingMode ?? order.ShippingMode;
            order.LogisticType = shipmentDetails.LogisticType ?? order.LogisticType;
            order.ShipByDeadlineAt = shipmentDetails.ShipByDeadlineAt ?? order.ShipByDeadlineAt;
            if (IsShippedStatus(shipmentDetails.Status))
            {
                order.Status = "shipped";
            }

            order.UpdatedAt = DateTimeOffset.UtcNow;
            shipment.MlOrderId ??= order.MlOrderId;
        }

        if (IsShippedStatus(shipment.Status))
        {
            await _auditLogService.RecordAsync(
                eventLog.TenantId,
                eventLog.ClientId,
                eventLog.Provider,
                eventLog.SellerId,
                MarketplaceEventTopics.AuditShipmentShipped,
                shipment.ShipmentId,
                new
                {
                    shipmentId = shipment.ShipmentId,
                    status = shipment.Status,
                    substatus = shipment.Substatus,
                    trackingNumber = shipment.TrackingNumber,
                    trackingUrl = shipment.TrackingUrl
                },
                "v1",
                cancellationToken);
        }
    }

    private static string BuildDedupeKey(string topic, string resourceId, long sellerId, string notificationId)
    {
        if (!string.IsNullOrWhiteSpace(notificationId))
        {
            return $"ml:notif:{notificationId}";
        }

        return $"ml:resource:{topic.ToLowerInvariant()}:{resourceId.ToLowerInvariant()}:{MercadoLivreSellerIdParser.ToApiString(sellerId)}";
    }

    private static string? ExtractResourceId(string resource, string segment)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            return null;
        }

        var candidate = resource.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            candidate = uri.AbsolutePath;
        }

        var clean = candidate.Split('?', '#')[0];
        var parts = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var segmentIndex = Array.FindIndex(parts, item => string.Equals(item, segment, StringComparison.OrdinalIgnoreCase));
        if (segmentIndex >= 0 && segmentIndex + 1 < parts.Length)
        {
            return parts[segmentIndex + 1].Trim();
        }

        return parts[^1].Trim();
    }

    private static bool SellerMatches(string? resolvedSellerId, long expectedSellerId)
    {
        if (!MercadoLivreSellerIdParser.TryParseRequired(resolvedSellerId, out var parsedSellerId))
        {
            return false;
        }

        return parsedSellerId == expectedSellerId;
    }

    private static bool IsShippedStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        var normalized = status.Trim().ToLowerInvariant();
        return normalized is "shipped" or "delivered";
    }

    private bool ValidateSecret(string? providedSecret)
    {
        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            return true;
        }

        return string.Equals(
            _options.WebhookSecret.Trim(),
            (providedSecret ?? string.Empty).Trim(),
            StringComparison.Ordinal);
    }

    private sealed record WebhookCandidate(Guid Id, string Status, int Attempts, DateTimeOffset? LastErrorAt);
}
