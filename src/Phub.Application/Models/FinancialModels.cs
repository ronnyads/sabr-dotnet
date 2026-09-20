using Phub.Domain.Enums;

namespace Phub.Application.Models;

public sealed class AppendFinancialEntryRequest
{
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public MarketplaceProvider Provider { get; set; } = MarketplaceProvider.MercadoLivre;
    public long SellerId { get; set; }
    public string EntryType { get; set; } = string.Empty;
    public string Layer { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long AmountCents { get; set; }
    public string CurrencyId { get; set; } = "BRL";
    public string EconomicKey { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public Guid? MarketplaceOrderId { get; set; }
    public Guid? MarketplaceOrderItemId { get; set; }
    public string? ExternalOrderId { get; set; }
    public string? ExternalPaymentId { get; set; }
    public string? ExternalShipmentId { get; set; }
    public string? ExternalPackId { get; set; }
    public string? ExternalClaimId { get; set; }
    public string? ExternalReturnId { get; set; }
    public DateTimeOffset EconomicOccurredAt { get; set; }
    public DateTimeOffset? FinancialConfirmedAt { get; set; }
    public DateTimeOffset? ProviderUpdatedAt { get; set; }
    public string SourceEndpoint { get; set; } = string.Empty;
    public string? SourceRecordId { get; set; }
    public string CanonicalPayloadHash { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";
}

public sealed class FinancialCoverageResult
{
    public decimal SkuPercent { get; set; }
    public decimal CostPercent { get; set; }
    public decimal FreightPercent { get; set; }
    public decimal OperationalPercent { get; set; }
    public decimal ConfirmedPercent { get; set; }
    public decimal ItemAllocationPercent { get; set; }
    public decimal OverallPercent { get; set; }
}

public sealed class FinancialDivergenceResult
{
    public long EstimatedCents { get; set; }
    public long ConfirmedCents { get; set; }
    public long AbsoluteCents { get; set; }
    public decimal? Percentage { get; set; }
    public Dictionary<string, long> ComponentsCents { get; set; } = new();
}

public sealed class ClientProfitabilityResult
{
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public DateTimeOffset? LastOperationalSyncAt { get; set; }
    public DateTimeOffset? LastBillingSyncAt { get; set; }
    public string CurrencyId { get; set; } = "BRL";
    public string Maturity { get; set; } = string.Empty;
    public long GrossRevenueCents { get; set; }
    public long MarketplaceNetAmountCents { get; set; }
    public long MarketplaceFeesCents { get; set; }
    public long SellerShippingCents { get; set; }
    public long RefundsCents { get; set; }
    public long AdjustmentsCents { get; set; }
    public long EstimatedEconomicNetCents { get; set; }
    public long ProductCostCents { get; set; }
    public string ProductCostMaturity { get; set; } = "INCOMPLETO";
    public long ReconciledConfirmedValueCents { get; set; }
    public long OperationalProfitCents { get; set; }
    public decimal? OperationalMarginPct { get; set; }
    public long SellerReportedEstimatedTaxCents { get; set; }
    public long ProfitAfterSellerTaxEstimateCents { get; set; }
    public long UnallocatedCents { get; set; }
    public FinancialCoverageResult Coverage { get; set; } = new();
    public FinancialDivergenceResult Divergence { get; set; } = new();
    public List<string> IncompleteReasons { get; set; } = new();
}

public class ClientProfitabilityOrderResult
{
    public Guid OrderId { get; set; }
    public string ExternalOrderId { get; set; } = string.Empty;
    public long SellerId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public DateTimeOffset EconomicDate { get; set; }
    public string Maturity { get; set; } = string.Empty;
    public long GrossRevenueCents { get; set; }
    public long EstimatedEconomicNetCents { get; set; }
    public long ConfirmedValueCents { get; set; }
    public long OperationalProfitCents { get; set; }
    public long UnallocatedCents { get; set; }
    public FinancialCoverageResult Coverage { get; set; } = new();
    public List<string> IncompleteReasons { get; set; } = new();
}

public sealed class ClientProfitabilityOrderDetailResult : ClientProfitabilityOrderResult
{
    public List<ClientProfitabilityEntryResult> Entries { get; set; } = new();
}

public sealed class ClientProfitabilityEntryResult
{
    public Guid EntryId { get; set; }
    public string EntryType { get; set; } = string.Empty;
    public string Layer { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long AmountCents { get; set; }
    public string CurrencyId { get; set; } = "BRL";
    public string EconomicKey { get; set; } = string.Empty;
    public bool IsActiveHead { get; set; }
    public Guid? SupersedesEntryId { get; set; }
    public DateTimeOffset EconomicOccurredAt { get; set; }
    public DateTimeOffset? FinancialConfirmedAt { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public string SourceEndpoint { get; set; } = string.Empty;
}

public sealed class SellerTaxProfileResult
{
    public long SellerId { get; set; }
    public int RateBasisPoints { get; set; }
    public decimal RatePercent => RateBasisPoints / 100m;
    public DateTimeOffset EffectiveFrom { get; set; }
    public long Version { get; set; }
    public string Disclaimer { get; set; } = "Imposto estimado informado pelo seller; não representa apuração fiscal.";
}

public sealed class UpdateSellerTaxProfileRequest
{
    public long SellerId { get; set; }
    public int RateBasisPoints { get; set; }
    public DateTimeOffset? EffectiveFrom { get; set; }
}

public sealed class FinancialSyncJobResult
{
    public Guid JobId { get; set; }
    public long SellerId { get; set; }
    public string JobType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset RangeFrom { get; set; }
    public DateTimeOffset RangeTo { get; set; }
    public int Total { get; set; }
    public int Processed { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class FinancialSyncEnqueueResult
{
    public List<FinancialSyncJobResult> Jobs { get; set; } = new();
}

public sealed class FinancialCapabilityResult
{
    public long SellerId { get; set; }
    public bool Orders { get; set; }
    public bool Shipments { get; set; }
    public bool Discounts { get; set; }
    public bool BillingMercadoLivre { get; set; }
    public bool BillingMercadoPago { get; set; }
    public bool RequiresReauthorization { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public List<string> Pending { get; set; } = new();
}
