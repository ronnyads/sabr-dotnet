namespace Phub.Application.Models;

public sealed class FinancialCostCorrectionDryRunRequest
{
    public long SellerId { get; set; }
    public List<FinancialCostCorrectionSkuRequest> Skus { get; set; } = [];
    public string Reason { get; set; } = string.Empty;
}

public sealed class FinancialCostCorrectionSkuRequest
{
    public string Sku { get; set; } = string.Empty;
    public long CorrectUnitCostCents { get; set; }
}

public sealed record FinancialCostCorrectionSkuReport(string Sku, int QuantityImpacted,
    long CurrentCostCents, long CorrectCostCents, long DifferenceCents, long ProfitImpactCents,
    int EntriesImpacted);

public sealed record FinancialCostCorrectionDryRunResult(Guid PlanId, string PlanHash,
    IReadOnlyCollection<FinancialCostCorrectionSkuReport> Skus, long TotalProfitImpactCents,
    int TotalEntries, string Status);
