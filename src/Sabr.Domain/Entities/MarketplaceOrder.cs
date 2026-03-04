using Sabr.Domain.Enums;

namespace Sabr.Domain.Entities;

public sealed class MarketplaceOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public MarketplaceProvider Provider { get; set; } = MarketplaceProvider.MercadoLivre;
    public long SellerId { get; set; }
    public string MlOrderId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset? PaidAt { get; set; }
    public string? ShipmentId { get; set; }
    public string? ShippingMode { get; set; }
    public string? LogisticType { get; set; }
    public DateTimeOffset? ShipByDeadlineAt { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SabrPaymentConfirmedAt { get; set; }
    public string? RiskFlagsJson { get; set; }
    public string? RawJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<MarketplaceOrderItem> Items { get; set; } = new List<MarketplaceOrderItem>();
}
