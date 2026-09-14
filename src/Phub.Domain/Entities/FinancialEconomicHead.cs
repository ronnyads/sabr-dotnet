using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

/// <summary>Current materialized head for one economic chain.</summary>
public sealed class FinancialEconomicHead
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public MarketplaceProvider Provider { get; set; } = MarketplaceProvider.MercadoLivre;
    public long SellerId { get; set; }
    public string EconomicKey { get; set; } = string.Empty;
    public Guid ActiveEntryId { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
