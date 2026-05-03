using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class MarketplaceAuditLogService
{
    private readonly IAppDbContext _dbContext;

    public MarketplaceAuditLogService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task RecordAsync(
        string tenantId,
        Guid clientId,
        MarketplaceProvider provider,
        long sellerId,
        string topic,
        string resourceId,
        object payload,
        string dedupeSuffix,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId)
            || clientId == Guid.Empty
            || string.IsNullOrWhiteSpace(topic)
            || string.IsNullOrWhiteSpace(resourceId))
        {
            return;
        }

        var normalizedTopic = topic.Trim().ToLowerInvariant();
        var normalizedResource = resourceId.Trim();
        var dedupe = $"audit:{provider}:{normalizedTopic}:{normalizedResource}:{dedupeSuffix.Trim()}";

        var exists = await _dbContext.MarketplaceEventLogs.AsNoTracking().AnyAsync(
            item => item.DedupeKey == dedupe,
            cancellationToken);
        if (exists)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _dbContext.MarketplaceEventLogs.Add(new MarketplaceEventLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Provider = provider,
            SellerId = sellerId,
            Topic = normalizedTopic,
            ResourceId = normalizedResource,
            DedupeKey = dedupe,
            Status = MarketplaceEventStatuses.Processed,
            Attempts = 1,
            ProcessedAt = now,
            PayloadJson = JsonSerializer.Serialize(payload),
            CreatedAt = now,
            UpdatedAt = now
        });
    }
}
