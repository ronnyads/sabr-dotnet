using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

/// <summary>Provider-owned shipment facts. Internal fulfillment actions must never write this projection.</summary>
public sealed class MarketplaceShipmentExternalState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public MarketplaceProvider Provider { get; set; } = MarketplaceProvider.MercadoLivre;
    public long SellerId { get; set; }
    public string ShipmentId { get; set; } = string.Empty;
    public string? MlOrderId { get; set; }
    public string? Status { get; set; }
    public string? Substatus { get; set; }
    public string? ShippingMode { get; set; }
    public string? LogisticType { get; set; }
    public DateTimeOffset? HandlingAt { get; set; }
    public DateTimeOffset? ReadyToShipAt { get; set; }
    public DateTimeOffset? FirstPrintedAt { get; set; }
    public DateTimeOffset? ShippedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? NotDeliveredAt { get; set; }
    public DateTimeOffset? ReturnedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset? DeliveryExpectedAt { get; set; }
    public string? DelayType { get; set; }
    public string? TrackingNumber { get; set; }
    public string? TrackingMethod { get; set; }
    public string? TrackingUrl { get; set; }
    public DateTimeOffset? ProviderUpdatedAt { get; set; }
    public DateTimeOffset LastMarketplaceSyncAt { get; set; }
    public string PayloadHash { get; set; } = string.Empty;
    public long Version { get; set; } = 1;
    public DateTimeOffset? NextReconciliationAt { get; set; }
    public int ReconciliationAttempts { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string FreshnessState { get; set; } = "FRESH";
    public string? LastSyncError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
