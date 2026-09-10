using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class SentinelService
{
    private static readonly SentinelRiskPolicyResult DefaultPolicy = new(240, 120, 60, 30);
    private readonly IAppDbContext _db;

    public SentinelService(IAppDbContext db) => _db = db;

    public async Task<SentinelShipmentPageResult> ListAsync(SentinelShipmentFilter filter, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var query = _db.MarketplaceShipmentExternalStates.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.TenantId)) query = query.Where(x => x.TenantId == filter.TenantId);
        if (filter.ClientId.HasValue) query = query.Where(x => x.ClientId == filter.ClientId);
        if (filter.SellerId.HasValue) query = query.Where(x => x.SellerId == filter.SellerId);
        if (filter.Provider.HasValue) query = query.Where(x => x.Provider == filter.Provider);
        if (filter.From.HasValue) query = query.Where(x => x.CreatedAt >= filter.From);
        if (filter.To.HasValue) query = query.Where(x => x.CreatedAt <= filter.To);

        var external = await query.OrderByDescending(x => x.UpdatedAt).Take(5000).ToListAsync(ct);
        if (external.Count == 0) return new(now, 0, []);

        var shipmentIds = external.Select(x => x.ShipmentId).Distinct().ToArray();
        var operational = await _db.MarketplaceShipmentOperationalStates.AsNoTracking()
            .Where(x => shipmentIds.Contains(x.ShipmentId)).ToListAsync(ct);
        var deadlines = await _db.MarketplaceShipmentDispatchDeadlineVersions.AsNoTracking()
            .Where(x => x.IsCurrent && shipmentIds.Contains(x.ShipmentId)).ToListAsync(ct);
        var orders = await _db.MarketplaceOrders.AsNoTracking()
            .Where(x => x.ShipmentId != null && shipmentIds.Contains(x.ShipmentId)).ToListAsync(ct);
        var clientIds = external.Select(x => x.ClientId).Distinct().ToArray();
        var tenantIds = external.Select(x => x.TenantId).Distinct().ToArray();
        var clients = await _db.Clients.AsNoTracking().Where(x => clientIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.TradeName ?? x.AccountName, ct);
        var policies = await _db.TenantMarketplaceSlaRules.AsNoTracking()
            .Where(x => tenantIds.Contains(x.TenantId) && clientIds.Contains(x.ClientId))
            .ToListAsync(ct);

        var opByKey = operational.ToDictionary(Key);
        var deadlineByKey = deadlines.ToDictionary(Key);
        var orderByKey = orders.GroupBy(x => $"{x.TenantId}|{x.ClientId}|{x.Provider}|{x.ShipmentId}")
            .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.ChannelCreatedAt).First());

        var items = external.Select(ext =>
        {
            opByKey.TryGetValue(Key(ext), out var op);
            deadlineByKey.TryGetValue(Key(ext), out var deadline);
            orderByKey.TryGetValue(Key(ext), out var order);
            clients.TryGetValue(ext.ClientId, out var clientName);
            return Map(ext, op, deadline, order, clientName ?? ext.ClientId.ToString(), now, ResolvePolicy(ext, policies));
        });

        if (!string.IsNullOrWhiteSpace(filter.Stage)) items = items.Where(x => x.InternalState.Equals(filter.Stage, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(filter.Risk)) items = items.Where(x => x.RiskLevel.Equals(filter.Risk, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(filter.Cause)) items = items.Where(x => x.Cause.Equals(filter.Cause, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(filter.Operator)) items = items.Where(x => x.Operator?.Contains(filter.Operator, StringComparison.OrdinalIgnoreCase) == true);

        var ordered = items.OrderBy(x => RiskOrder(x.RiskLevel)).ThenBy(x => x.DispatchDeadline ?? DateTimeOffset.MaxValue).ToList();
        return new(now, ordered.Count, ordered.Skip(Math.Max(filter.Skip, 0)).Take(Math.Clamp(filter.Limit, 1, 200)).ToList());
    }

    public async Task<SentinelSummaryResult> SummaryAsync(SentinelShipmentFilter filter, CancellationToken ct)
    {
        var page = await ListAsync(filter with { Skip = 0, Limit = 5000 }, ct);
        var items = page.Items;
        var saoPaulo = ResolveSaoPauloTimeZone();
        var localToday = TimeZoneInfo.ConvertTime(page.ServerNow, saoPaulo).Date;
        return new(page.ServerNow, items.Count,
            items.Count(x => x.DispatchDeadline.HasValue && TimeZoneInfo.ConvertTime(x.DispatchDeadline.Value, saoPaulo).Date == localToday),
            items.Count(x => x.InternalState is "PICKING" or "SEPARATED"),
            items.Count(x => x.InternalState == "PACKED" && x.ExternalState != "SHIPPED"),
            items.Count(x => x.RiskLevel == "CRITICAL"),
            items.Count(x => x.RiskLevel == "OVERDUE"),
            items.Count(x => x.Freshness is "STALE" or "INTEGRATION_RISK"),
            [.. items.GroupBy(x => x.RiskLevel).Select(x => new SentinelHorizonBucketResult(x.Key, x.Count())).OrderBy(x => RiskOrder(x.Level))]);
    }

    public async Task<SentinelShipmentDetailResult?> DetailAsync(string shipmentId, CancellationToken ct)
    {
        var page = await ListAsync(new(null, null, null, null, null, null, null, null, null, null, 0, 5000), ct);
        var item = page.Items.FirstOrDefault(x => x.ShipmentId == shipmentId);
        if (item is null) return null;
        var timeline = new List<SentinelTimelineItemResult>();
        Add(timeline, "external", "Última confirmação do marketplace", item.LastMarketplaceSyncAt, "marketplace", null);
        Add(timeline, "internal", "Etiqueta impressa", item.LabelPrintedAt, "PrometheusHUB", item.Operator);
        Add(timeline, "internal", "Separação iniciada", item.PickingStartedAt, "PrometheusHUB", item.Operator);
        Add(timeline, "internal", "Separado", item.SeparatedAt, "PrometheusHUB", item.Operator);
        Add(timeline, "internal", "Embalado e pronto", item.PackedAt, "PrometheusHUB", item.Operator);
        var history = await _db.MarketplaceShipmentDispatchDeadlineVersions.AsNoTracking()
            .Where(x => x.ShipmentId == shipmentId && x.ClientId == item.ClientId && x.Provider == item.Provider)
            .OrderByDescending(x => x.Version)
            .Select(x => new SentinelDeadlineVersionResult(x.Version, x.DispatchDeadline, x.Source, x.QueriedAt, x.ProviderLastUpdatedAt, x.PayloadHash, x.IsCurrent))
            .ToListAsync(ct);
        return new(page.ServerNow, item, timeline.OrderBy(x => x.At).ToList(), history);
    }

    public async Task<SentinelRiskPolicyResult> GetPolicyAsync(CancellationToken ct)
    {
        var rule = await _db.TenantMarketplaceSlaRules.AsNoTracking().OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync(ct);
        return rule is null ? DefaultPolicy : new(rule.MonitorMinutes, rule.AttentionMinutes, rule.UrgentMinutes, rule.CriticalMinutes);
    }

    public async Task<SentinelRiskPolicyResult> UpdatePolicyAsync(SentinelRiskPolicyUpdateRequest request, CancellationToken ct)
    {
        if (!(request.MonitorMinutes > request.AttentionMinutes && request.AttentionMinutes > request.UrgentMinutes && request.UrgentMinutes > request.CriticalMinutes && request.CriticalMinutes > 0))
            throw new ArgumentException("Os limites devem ser decrescentes e maiores que zero.");
        var rules = await _db.TenantMarketplaceSlaRules.ToListAsync(ct);
        foreach (var rule in rules)
        {
            rule.MonitorMinutes = request.MonitorMinutes;
            rule.AttentionMinutes = request.AttentionMinutes;
            rule.UrgentMinutes = request.UrgentMinutes;
            rule.CriticalMinutes = request.CriticalMinutes;
            rule.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        return new(request.MonitorMinutes, request.AttentionMinutes, request.UrgentMinutes, request.CriticalMinutes);
    }

    public async Task<int> EmitLevelChangesAsync(CancellationToken ct)
    {
        var page = await ListAsync(new(null, null, null, null, null, null, null, null, null, null, 0, 5000), ct);
        var emitted = 0;
        foreach (var item in page.Items.Where(x => x.DeadlineVersion.HasValue))
        {
            var dedupKey = $"sentinel:sla:{item.Provider}:{item.SellerId}:{item.ShipmentId}:{item.DeadlineVersion}:{item.RiskLevel}";
            if (await _db.MarketplaceEventLogs.AsNoTracking().AnyAsync(x => x.DedupeKey == dedupKey, ct)) continue;
            var previousPayload = await _db.MarketplaceEventLogs.AsNoTracking()
                .Where(x => x.Topic == MarketplaceEventTopics.SentinelSlaLevelChanged && x.ResourceId == item.ShipmentId)
                .OrderByDescending(x => x.CreatedAt).Select(x => x.PayloadJson).FirstOrDefaultAsync(ct);
            string? previousLevel = null;
            if (!string.IsNullOrWhiteSpace(previousPayload))
            {
                try
                {
                    using var document = JsonDocument.Parse(previousPayload);
                    if (document.RootElement.TryGetProperty("newLevel", out var level)) previousLevel = level.GetString();
                }
                catch (JsonException) { }
            }
            _db.MarketplaceEventLogs.Add(new MarketplaceEventLog
            {
                TenantId = await ResolveTenantIdAsync(item, ct), ClientId = item.ClientId, Provider = item.Provider,
                SellerId = item.SellerId, Topic = MarketplaceEventTopics.SentinelSlaLevelChanged,
                ResourceId = item.ShipmentId, DedupeKey = dedupKey, Status = MarketplaceEventStatuses.Pending,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    tenant = await ResolveTenantIdAsync(item, ct), clientId = item.ClientId, provider = item.Provider.ToString(),
                    sellerId = item.SellerId, orderId = item.OrderId, shipmentId = item.ShipmentId,
                    deadlineVersion = item.DeadlineVersion, deadlineHash = item.DeadlineHash,
                    deadline = item.DispatchDeadline, evaluatedAt = page.ServerNow,
                    previousLevel, newLevel = item.RiskLevel, minutesRemaining = item.MinutesRemaining,
                    internalState = item.InternalState, externalState = item.ExternalState,
                    cause = item.Cause, freshness = item.Freshness,
                    lastMarketplaceSyncAt = item.LastMarketplaceSyncAt, dedupKey
                })
            });
            emitted++;
        }
        if (emitted > 0) await _db.SaveChangesAsync(ct);
        return emitted;
    }

    private async Task<string> ResolveTenantIdAsync(SentinelShipmentResult item, CancellationToken ct)
        => await _db.MarketplaceShipmentExternalStates.AsNoTracking()
            .Where(x => x.ClientId == item.ClientId && x.Provider == item.Provider && x.ShipmentId == item.ShipmentId)
            .Select(x => x.TenantId).FirstAsync(ct);

    private static SentinelShipmentResult Map(MarketplaceShipmentExternalState ext, MarketplaceShipmentOperationalState? op,
        MarketplaceShipmentDispatchDeadlineVersion? deadline, MarketplaceOrder? order, string clientName, DateTimeOffset now, SentinelRiskPolicyResult policy)
    {
        var expectedMinutes = op?.PackedAt != null ? 1 : 5;
        var age = now - ext.LastMarketplaceSyncAt;
        var freshness = ext.LastSyncError is not null || ext.ReconciliationAttempts >= 3 ? "INTEGRATION_RISK"
            : age > TimeSpan.FromMinutes(Math.Max(expectedMinutes * 3, 5)) ? "STALE" : "FRESH";
        var minutes = deadline is null ? (double?)null : (deadline.DispatchDeadline - now).TotalMinutes;
        var risk = freshness != "FRESH" ? freshness : Risk(minutes, policy);
        var internalState = op?.PackedAt != null ? "PACKED" : op?.SeparatedAt != null ? "SEPARATED" : op?.PickingStartedAt != null ? "PICKING" : op?.LabelPrintedAt != null ? "LABEL_PRINTED" : "RECEIVED";
        var externalState = NormalizeExternal(ext);
        var cause = freshness != "FRESH" ? "INTEGRATION_DIVERGENCE"
            : internalState == "PACKED" && externalState != "SHIPPED" ? "PACKED_WITHOUT_CONFIRMATION"
            : string.IsNullOrWhiteSpace(ext.Status) ? "LABEL_UNAVAILABLE"
            : "INTERNAL_OPERATION";
        var actor = op?.PackedBy ?? op?.SeparatedBy ?? op?.PickingStartedBy ?? op?.LabelPrintedBy;
        return new(ext.ShipmentId, ext.ClientId, clientName, ext.Provider, ext.SellerId, order?.MlOrderId ?? ext.MlOrderId,
            order?.InternalOrderNumber, internalState, externalState, risk, cause, freshness, deadline?.DispatchDeadline,
            deadline?.Version, deadline?.PayloadHash, deadline?.Source, deadline?.QueriedAt, ext.LastMarketplaceSyncAt,
            minutes, actor, ext.FirstPrintedAt.HasValue || op?.LabelPrintedAt.HasValue == true,
            op?.LabelPrintedAt, op?.PickingStartedAt, op?.SeparatedAt, op?.PackedAt);
    }

    private static string Risk(double? minutes, SentinelRiskPolicyResult p)
    {
        if (!minutes.HasValue) return "NO_DEADLINE";
        if (minutes < 0) return "OVERDUE";
        if (minutes <= p.CriticalMinutes) return "CRITICAL";
        if (minutes <= p.UrgentMinutes) return "URGENT";
        if (minutes <= p.AttentionMinutes) return "ATTENTION";
        if (minutes <= p.MonitorMinutes) return "MONITOR";
        return "NORMAL";
    }

    private static SentinelRiskPolicyResult ResolvePolicy(
        MarketplaceShipmentExternalState shipment,
        IReadOnlyCollection<TenantMarketplaceSlaRule> policies)
    {
        var candidates = policies.Where(x => x.TenantId == shipment.TenantId
                                              && x.ClientId == shipment.ClientId
                                              && x.Provider == shipment.Provider
                                              && (string.IsNullOrWhiteSpace(x.LogisticType)
                                                  || string.Equals(x.LogisticType, shipment.LogisticType, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => !string.IsNullOrWhiteSpace(x.LogisticType))
            .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.ShippingMode)
                                   && string.Equals(x.ShippingMode, shipment.ShippingMode, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var rule = candidates.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.ShippingMode)
                                                   || string.Equals(x.ShippingMode, shipment.ShippingMode, StringComparison.OrdinalIgnoreCase));
        return rule is null
            ? DefaultPolicy
            : new(rule.MonitorMinutes, rule.AttentionMinutes, rule.UrgentMinutes, rule.CriticalMinutes);
    }

    private static int RiskOrder(string risk) => risk switch { "INTEGRATION_RISK" => 0, "OVERDUE" => 1, "CRITICAL" => 2, "STALE" => 3, "URGENT" => 4, "ATTENTION" => 5, "MONITOR" => 6, "NORMAL" => 7, _ => 8 };
    private static string NormalizeExternal(MarketplaceShipmentExternalState x) => x.CancelledAt.HasValue ? "CANCELLED" : x.ReturnedAt.HasValue ? "RETURNED" : x.DeliveredAt.HasValue ? "DELIVERED" : x.ShippedAt.HasValue ? "SHIPPED" : (x.Status ?? "UNKNOWN").ToUpperInvariant();
    private static string Key(MarketplaceShipmentExternalState x) => $"{x.TenantId}|{x.ClientId}|{x.Provider}|{x.ShipmentId}";
    private static string Key(MarketplaceShipmentOperationalState x) => $"{x.TenantId}|{x.ClientId}|{x.Provider}|{x.ShipmentId}";
    private static string Key(MarketplaceShipmentDispatchDeadlineVersion x) => $"{x.TenantId}|{x.ClientId}|{x.Provider}|{x.ShipmentId}";
    private static void Add(List<SentinelTimelineItemResult> list, string type, string label, DateTimeOffset? at, string source, string? actor) { if (at.HasValue) list.Add(new(type, label, at.Value, source, actor)); }
    private static TimeZoneInfo ResolveSaoPauloTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time"); }
    }
}
