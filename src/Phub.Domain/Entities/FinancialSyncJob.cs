using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

public sealed class FinancialSyncJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ParentJobId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public MarketplaceProvider Provider { get; set; } = MarketplaceProvider.MercadoLivre;
    public long SellerId { get; set; }
    public string JobType { get; set; } = string.Empty;
    public string Status { get; set; } = "PENDING";
    public DateTimeOffset RangeFrom { get; set; }
    public DateTimeOffset RangeTo { get; set; }
    public string? Checkpoint { get; set; }
    public string DedupeKey { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string ResultJson { get; set; } = "{}";
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public static class FinancialSyncJobTypes
{
    public const string OperationalSyncBatch = "OPERATIONAL_SYNC_BATCH";
    public const string OperationalSyncChunk = "OPERATIONAL_SYNC_CHUNK";
    public const string BillingReconciliation = "BILLING_RECONCILIATION";
    public const string BillingReconciliationBatch = "BILLING_RECONCILIATION_BATCH";
}
