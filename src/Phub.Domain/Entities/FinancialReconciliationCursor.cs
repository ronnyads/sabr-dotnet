using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

public sealed class FinancialReconciliationCursor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public MarketplaceProvider Provider { get; set; } = MarketplaceProvider.MercadoLivre;
    public long SellerId { get; set; }
    public string BillingGroup { get; set; } = string.Empty;
    public string PeriodKey { get; set; } = string.Empty;
    public string? FromId { get; set; }
    public string Status { get; set; } = "PENDING";
    public string? LastPayloadHash { get; set; }
    public DateTimeOffset? LastSucceededAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
