using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

public sealed class TenantMarketplaceSlaRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public MarketplaceProvider Provider { get; set; } = MarketplaceProvider.MercadoLivre;
    public string LogisticType { get; set; } = string.Empty;
    public string? ShippingMode { get; set; }
    public string CutoffLocalTime { get; set; } = "12:00";
    public int MonitorMinutes { get; set; } = 240;
    public int AttentionMinutes { get; set; } = 120;
    public int UrgentMinutes { get; set; } = 60;
    public int CriticalMinutes { get; set; } = 30;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
