namespace Phub.Domain.Entities;

public sealed class FinancialCorrectionPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public long SellerId { get; set; }
    public string Status { get; set; } = FinancialCorrectionPlanStatuses.DryRun;
    public string PlanHash { get; set; } = string.Empty;
    public string ScopeJson { get; set; } = "{}";
    public string ReportJson { get; set; } = "{}";
    public string Reason { get; set; } = string.Empty;
    public int TotalEntries { get; set; }
    public int ProcessedEntries { get; set; }
    public Guid? LastProcessedEntryId { get; set; }
    public string? LastError { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FinancialCorrectionPlanEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlanId { get; set; }
    public Guid OriginalEntryId { get; set; }
    public Guid ReplacementEntryId { get; set; }
    public Guid? MarketplaceOrderId { get; set; }
    public string EconomicKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class FinancialCorrectionPlanStatuses
{
    public const string DryRun = "DRY_RUN";
    public const string Approved = "APPROVED";
    public const string Running = "RUNNING";
    public const string Reconciling = "RECONCILING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
}
