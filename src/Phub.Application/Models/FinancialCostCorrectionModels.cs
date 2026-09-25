namespace Phub.Application.Models;

public sealed class FinancialCostCorrectionDryRunRequest
{
    public long SellerId { get; set; }
    public List<FinancialCostCorrectionSkuRequest> Skus { get; set; } = [];
    public DateTimeOffset? RangeFrom { get; set; }
    public DateTimeOffset? RangeToExclusive { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class FinancialCostCorrectionSkuRequest
{
    public string Sku { get; set; } = string.Empty;
    public long CorrectUnitCostCents { get; set; }
    public List<Guid> IncorrectCatalogPriceVersionIds { get; set; } = [];
}

public sealed record FinancialCostCorrectionSkuReport(string Sku, int QuantityImpacted,
    long CurrentCostCents, long CorrectCostCents, long DifferenceCents, long ProfitImpactCents,
    int EntriesImpacted, int CatalogQuantityCorrected, int PrePurchasedQuantityPreserved);

public sealed record FinancialCostCorrectionManifestEntry(
    string EconomicKey,
    Guid ExpectedHeadId,
    Guid ExpectedActiveEntryId,
    long ExpectedHeadVersion,
    string ExpectedActiveEntryHash,
    Guid ExpectedPriceVersionId,
    string ExpectedCostReferencesHash,
    Guid MarketplaceOrderId,
    Guid MarketplaceOrderItemId,
    string Sku,
    DateTimeOffset EconomicAt,
    string EconomicAtSource,
    string CostSource,
    int CatalogQuantity,
    int PrePurchasedQuantity,
    long CurrentCostCents,
    long ReplacementCostCents,
    long ProfitImpactCents,
    string ReplacementBreakdownJson);

public sealed record FinancialCostCorrectionPendingItem(
    string EconomicKey,
    Guid? MarketplaceOrderId,
    Guid? MarketplaceOrderItemId,
    string? Sku,
    string Code,
    string Detail,
    Guid? ObservedPriceVersionId = null,
    string? ObservedCostSource = null,
    long? ObservedCostCents = null);

public sealed record FinancialCorrectionCoverage(long Resolved, long Total, decimal Percent);

public sealed record FinancialCostCorrectionReport(
    DateTimeOffset RangeFrom,
    DateTimeOffset RangeToExclusive,
    IReadOnlyCollection<FinancialCostCorrectionSkuReport> Skus,
    IReadOnlyCollection<FinancialCostCorrectionManifestEntry> Manifest,
    IReadOnlyCollection<FinancialCostCorrectionPendingItem> Pending,
    FinancialCorrectionCoverage CostCoverageBefore,
    FinancialCorrectionCoverage CostCoverageAfter,
    FinancialCorrectionCoverage FinancialCoverage,
    long TotalProfitImpactCents,
    int TotalEntries);

public sealed record FinancialCostCorrectionDryRunResult(Guid PlanId, string PlanHash,
    FinancialCostCorrectionReport Report, string Status);

public sealed record FinancialCostCorrectionPlanResult(Guid PlanId, string PlanHash, string Status,
    FinancialCostCorrectionReport Report, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed class FinancialCorrectionPlanCommand
{
    public string PlanHash { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed record FinancialCorrectionPlanCommandResult(
    Guid PlanId,
    string PlanHash,
    string Status,
    int TotalEntries,
    int ProcessedEntries,
    string? LastError);

public sealed class FinancialCorrectionCommandRequest
{
    public string Reason { get; set; } = string.Empty;
}
