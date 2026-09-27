namespace Phub.Domain.Entities;

/// <summary>
/// SuperAdmin-approved historical catalog cost used only before <see cref="BaselineCutAt"/>.
/// A staged baseline is inert until its financial correction plan is atomically activated.
/// </summary>
public sealed class CatalogCostBaseline
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlanId { get; set; }
    public string ProductSku { get; set; } = string.Empty;
    public string VariantSku { get; set; } = string.Empty;
    public Guid BaselinePriceVersionId { get; set; }
    public long BaselineUnitCostCents { get; set; }
    public DateTimeOffset BaselineCutAt { get; set; }
    public string CostOrigin { get; set; } = CatalogCostBaselineOrigins.ApprovedRetroactiveBaseline;
    public string Status { get; set; } = CatalogCostBaselineStatuses.Staged;
    public Guid ApprovedByUserId { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string PlanHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ActivatedAt { get; set; }
}

public static class CatalogCostBaselineStatuses
{
    public const string Staged = "STAGED";
    public const string Active = "ACTIVE";
    public const string Discarded = "DISCARDED";
}

public static class CatalogCostBaselineOrigins
{
    public const string ApprovedRetroactiveBaseline = "APPROVED_RETROACTIVE_BASELINE";
}

public static class CatalogCostBaselinePolicy
{
    public static void EnsureValid(CatalogCostBaseline baseline)
    {
        if (string.IsNullOrWhiteSpace(baseline.ProductSku) || string.IsNullOrWhiteSpace(baseline.VariantSku))
            throw new InvalidOperationException("A catalog cost baseline requires product and variant SKUs.");
        if (baseline.BaselineUnitCostCents <= 0)
            throw new InvalidOperationException("A catalog cost baseline must be greater than zero.");
        if (baseline.BaselineCutAt == default)
            throw new InvalidOperationException("A catalog cost baseline requires a cut timestamp.");
        if (baseline.PlanId == Guid.Empty || baseline.BaselinePriceVersionId == Guid.Empty
            || baseline.ApprovedByUserId == Guid.Empty || string.IsNullOrWhiteSpace(baseline.Reason)
            || string.IsNullOrWhiteSpace(baseline.PlanHash))
            throw new InvalidOperationException("A catalog cost baseline requires plan, approver and reason.");
        if (baseline.Status == CatalogCostBaselineStatuses.Active && !baseline.ApprovedAt.HasValue)
            throw new InvalidOperationException("An active catalog cost baseline requires approval timestamp.");
        if (baseline.CostOrigin != CatalogCostBaselineOrigins.ApprovedRetroactiveBaseline)
            throw new InvalidOperationException($"Unsupported catalog baseline origin '{baseline.CostOrigin}'.");
        if (baseline.Status is not (CatalogCostBaselineStatuses.Staged or CatalogCostBaselineStatuses.Active
            or CatalogCostBaselineStatuses.Discarded))
            throw new InvalidOperationException($"Unsupported catalog baseline status '{baseline.Status}'.");
    }
}
