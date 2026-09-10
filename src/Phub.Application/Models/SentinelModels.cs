using Phub.Domain.Enums;

namespace Phub.Application.Models;

public sealed record SentinelSummaryResult(
    DateTimeOffset ServerNow,
    int TotalOpen,
    int DueToday,
    int Picking,
    int PackedAwaitingConfirmation,
    int Critical,
    int Overdue,
    int IntegrationRisk,
    IReadOnlyList<SentinelHorizonBucketResult> Horizon);

public sealed record SentinelHorizonBucketResult(string Level, int Count);

public sealed record SentinelShipmentResult(
    string ShipmentId,
    Guid ClientId,
    string ClientName,
    MarketplaceProvider Provider,
    long SellerId,
    string? OrderId,
    string? InternalOrderNumber,
    string InternalState,
    string ExternalState,
    string RiskLevel,
    string Cause,
    string Freshness,
    DateTimeOffset? DispatchDeadline,
    long? DeadlineVersion,
    string? DeadlineHash,
    string? DeadlineSource,
    DateTimeOffset? DeadlineQueriedAt,
    DateTimeOffset? LastMarketplaceSyncAt,
    double? MinutesRemaining,
    string? Operator,
    bool LabelAvailable,
    DateTimeOffset? LabelPrintedAt,
    DateTimeOffset? PickingStartedAt,
    DateTimeOffset? SeparatedAt,
    DateTimeOffset? PackedAt);

public sealed record SentinelShipmentDetailResult(
    DateTimeOffset ServerNow,
    SentinelShipmentResult Shipment,
    IReadOnlyList<SentinelTimelineItemResult> Timeline,
    IReadOnlyList<SentinelDeadlineVersionResult> DeadlineHistory);

public sealed record SentinelTimelineItemResult(string Type, string Label, DateTimeOffset At, string Source, string? Actor);

public sealed record SentinelDeadlineVersionResult(
    long Version,
    DateTimeOffset DispatchDeadline,
    string Source,
    DateTimeOffset QueriedAt,
    DateTimeOffset? ProviderLastUpdatedAt,
    string PayloadHash,
    bool IsCurrent);

public sealed record SentinelRiskPolicyResult(int MonitorMinutes, int AttentionMinutes, int UrgentMinutes, int CriticalMinutes);

public sealed record SentinelRiskPolicyUpdateRequest(int MonitorMinutes, int AttentionMinutes, int UrgentMinutes, int CriticalMinutes);

public sealed record SentinelShipmentFilter(
    string? TenantId,
    Guid? ClientId,
    long? SellerId,
    MarketplaceProvider? Provider,
    string? Stage,
    string? Risk,
    string? Cause,
    string? Operator,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Skip,
    int Limit);

public sealed record SentinelShipmentPageResult(DateTimeOffset ServerNow, int Total, IReadOnlyList<SentinelShipmentResult> Items);
