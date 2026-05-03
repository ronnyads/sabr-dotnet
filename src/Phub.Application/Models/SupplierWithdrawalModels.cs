namespace Phub.Application.Models;

public sealed class SupplierWithdrawalRequest
{
    public long RequestedAmountCents { get; set; }
}

public sealed class AdminApproveWithdrawalRequest
{
    public string? Notes { get; set; }
}

public sealed class AdminRejectWithdrawalRequest
{
    public string Reason { get; set; } = string.Empty;
}

public sealed class SupplierWithdrawalResult
{
    public Guid Id { get; set; }
    public Guid SupplierId { get; set; }
    public long RequestedAmountCents { get; set; }
    public long FeeAmountCents { get; set; }
    public long NetAmountCents { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? BankDetailsSnapshot { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
    public string? RejectionReason { get; set; }
    public string? Notes { get; set; }
}

public sealed class SupplierWithdrawalsResult
{
    public List<SupplierWithdrawalResult> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
