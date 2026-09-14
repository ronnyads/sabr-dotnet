using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

public sealed class MarketplaceOrderFinancialState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MarketplaceOrderId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public MarketplaceProvider Provider { get; set; } = MarketplaceProvider.MercadoLivre;
    public long SellerId { get; set; }
    public string Maturity { get; set; } = FinancialMaturity.Incomplete;
    public long GrossRevenueCents { get; set; }
    public long EstimatedEconomicNetCents { get; set; }
    public long ConfirmedValueCents { get; set; }
    public long OperationalProfitCents { get; set; }
    public long UnallocatedCents { get; set; }
    public bool SkuResolved { get; set; }
    public bool CostResolved { get; set; }
    public bool FreightResolved { get; set; }
    public bool OperationalComponentsResolved { get; set; }
    public bool ConfirmedComponentsResolved { get; set; }
    public bool ItemAllocationResolved { get; set; }
    public string IncompleteReasonsJson { get; set; } = "[]";
    public string DivergenceJson { get; set; } = "{}";
    public DateTimeOffset? WasConfirmedAt { get; set; }
    public DateTimeOffset? ReopenedAt { get; set; }
    public DateTimeOffset LastProjectedAt { get; set; } = DateTimeOffset.UtcNow;
    public long Version { get; set; } = 1;
}
