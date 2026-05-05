using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

public sealed class SupplierWithdrawal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    public long RequestedAmountCents { get; set; }
    public long FeeAmountCents { get; set; }
    public long NetAmountCents { get; set; }
    public SupplierWithdrawalStatus Status { get; set; } = SupplierWithdrawalStatus.Pending;
    public string? BankDetailsSnapshot { get; set; }
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? ApprovedByPlatformUserId { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
    public string? RejectionReason { get; set; }
    public string? Notes { get; set; }
}
