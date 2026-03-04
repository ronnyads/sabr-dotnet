using Sabr.Domain.Enums;

namespace Sabr.Domain.Entities;

public sealed class MarketplaceEventLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public MarketplaceProvider Provider { get; set; } = MarketplaceProvider.MercadoLivre;
    public long SellerId { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string? NotificationId { get; set; }
    public string DedupeKey { get; set; } = string.Empty;
    public string Status { get; set; } = "PENDING";
    public int Attempts { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public string? LastError { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
