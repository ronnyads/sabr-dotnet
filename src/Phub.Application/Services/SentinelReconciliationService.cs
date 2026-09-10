using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Entities;

namespace Phub.Application.Services;

/// <summary>
/// Durable reconciliation scheduler. Webhooks remain primary; this only enqueues missed
/// shipment reads and uses a database lease so replicas cannot enqueue the same work.
/// </summary>
public sealed class SentinelReconciliationService
{
    private readonly IAppDbContext _db;
    private readonly string _workerId = $"sentinel-{Environment.MachineName}-{Guid.NewGuid():N}";
    public SentinelReconciliationService(IAppDbContext db) => _db = db;

    public async Task<int> EnqueueDueAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = await _db.MarketplaceShipmentExternalStates.AsNoTracking()
            .Where(x => x.ShippedAt == null && (x.NextReconciliationAt == null || x.NextReconciliationAt <= now)
                        && (x.LeaseUntil == null || x.LeaseUntil < now))
            .OrderBy(x => x.NextReconciliationAt)
            .Take(100)
            .ToListAsync(ct);
        var selected = candidates.GroupBy(x => new { x.Provider, x.SellerId }).SelectMany(g => g.Take(10)).Take(40);
        var count = 0;
        foreach (var candidate in selected)
        {
            var claimed = await _db.MarketplaceShipmentExternalStates
                .Where(x => x.Id == candidate.Id && (x.LeaseUntil == null || x.LeaseUntil < now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.LockedBy, _workerId)
                    .SetProperty(x => x.LeaseUntil, now.AddSeconds(45)), ct);
            if (claimed != 1) continue;

            var packed = await _db.MarketplaceShipmentOperationalStates.AsNoTracking()
                .AnyAsync(x => x.TenantId == candidate.TenantId && x.ClientId == candidate.ClientId
                               && x.Provider == candidate.Provider && x.ShipmentId == candidate.ShipmentId
                               && x.PackedAt != null, ct);
            var notificationId = $"sentinel:{candidate.ShipmentId}:{now:yyyyMMddHHmm}";
            _db.MarketplaceEventLogs.Add(new MarketplaceEventLog
            {
                TenantId = candidate.TenantId, ClientId = candidate.ClientId, Provider = candidate.Provider,
                SellerId = candidate.SellerId, Topic = MarketplaceEventTopics.WebhookShipments,
                ResourceId = $"/shipments/{candidate.ShipmentId}", NotificationId = notificationId,
                DedupeKey = $"sentinel:reconcile:{candidate.Provider}:{candidate.SellerId}:{candidate.ShipmentId}:{now:yyyyMMddHHmm}",
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { source = "sentinel_reconciliation", shipmentId = candidate.ShipmentId, evaluatedAt = now })
            });
            await _db.MarketplaceShipmentExternalStates.Where(x => x.Id == candidate.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.NextReconciliationAt, now.AddMinutes(packed ? 1 : 5))
                    .SetProperty(x => x.LockedBy, (string?)null)
                    .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null), ct);
            count++;
        }
        await _db.SaveChangesAsync(ct);
        return count;
    }
}
