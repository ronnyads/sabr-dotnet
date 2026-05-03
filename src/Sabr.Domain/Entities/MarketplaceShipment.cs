using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

public sealed class MarketplaceShipment
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
    public string? TrackingNumber { get; set; }
    public string? TrackingMethod { get; set; }
    public string? TrackingUrl { get; set; }
    public DateTimeOffset? ShippedAt { get; set; }
    public DateTimeOffset? ShipByDeadlineAt { get; set; }
    public string? LabelInternalUrl { get; set; }
    public string? LabelSourceUrl { get; set; }
    public string? LabelSha256 { get; set; }
    public string? LabelContentType { get; set; }
    public byte[]? LabelContentBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
