using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

/// <summary>PrometheusHUB-owned operational facts. Marketplace synchronization must never write these timestamps.</summary>
public sealed class MarketplaceShipmentOperationalState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public MarketplaceProvider Provider { get; set; } = MarketplaceProvider.MercadoLivre;
    public long SellerId { get; set; }
    public string ShipmentId { get; set; } = string.Empty;
    public DateTimeOffset? LabelPrintedAt { get; set; }
    public string? LabelPrintedBy { get; set; }
    public DateTimeOffset? PickingStartedAt { get; set; }
    public string? PickingStartedBy { get; set; }
    public DateTimeOffset? SeparatedAt { get; set; }
    public string? SeparatedBy { get; set; }
    public DateTimeOffset? PackedAt { get; set; }
    public string? PackedBy { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
