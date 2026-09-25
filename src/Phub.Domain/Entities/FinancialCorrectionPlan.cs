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
    public Guid? ReplacementEntryId { get; set; }
    public Guid? MarketplaceOrderId { get; set; }
    public string EconomicKey { get; set; } = string.Empty;
    public string State { get; set; } = FinancialCorrectionEntryStates.Staged;
    public Guid ExpectedHeadId { get; set; }
    public Guid ExpectedActiveEntryId { get; set; }
    public long ExpectedHeadVersion { get; set; }
    public string ExpectedActiveEntryHash { get; set; } = string.Empty;
    public Guid? ExpectedPriceVersionId { get; set; }
    public string ExpectedCostReferencesHash { get; set; } = string.Empty;
    public long CurrentAmountCents { get; set; }
    public long ReplacementAmountCents { get; set; }
    public string ReplacementBreakdownJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class FinancialCorrectionPlanStatuses
{
    public const string DryRun = "DRY_RUN";
    public const string AwaitingApproval = "AWAITING_APPROVAL";
    public const string Preparing = "PREPARING";
    public const string PendingActivation = "PENDING_ACTIVATION";
    public const string Activating = "ACTIVATING";
    public const string Reconciling = "RECONCILING";
    public const string Completed = "COMPLETED";
    public const string Stale = "STALE";
    public const string Failed = "FAILED";
}

public static class FinancialCorrectionEntryStates
{
    public const string Staged = "STAGED";
    public const string PendingActivation = "PENDING_ACTIVATION";
    public const string Active = "ACTIVE";
    public const string Discarded = "DISCARDED";
}
