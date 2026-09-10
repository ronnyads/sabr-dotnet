using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

/// <summary>Append-only official dispatch SLA history.</summary>
public sealed class MarketplaceShipmentDispatchDeadlineVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public MarketplaceProvider Provider { get; set; } = MarketplaceProvider.MercadoLivre;
    public long SellerId { get; set; }
    public string ShipmentId { get; set; } = string.Empty;
    public DateTimeOffset DispatchDeadline { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset? ProviderLastUpdatedAt { get; set; }
    public DateTimeOffset QueriedAt { get; set; }
    public long Version { get; set; }
    public string PayloadHash { get; set; } = string.Empty;
    public bool IsCurrent { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
