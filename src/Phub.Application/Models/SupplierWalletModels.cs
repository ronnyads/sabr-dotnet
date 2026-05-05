namespace Phub.Application.Models;

public sealed class SupplierWalletSummaryResult
{
    public Guid SupplierId { get; set; }
    public long PendingBalanceCents { get; set; }
    public long BlockedBalanceCents { get; set; }
    public long AvailableBalanceCents { get; set; }
    public long TotalBalanceCents => PendingBalanceCents + BlockedBalanceCents + AvailableBalanceCents;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SupplierWalletEntryResult
{
    public Guid Id { get; set; }
    public Guid SupplierId { get; set; }
    public Guid? OrderId { get; set; }
    public string Type { get; set; } = string.Empty;
    public long AmountCents { get; set; }
    public long BalanceAfterCents { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ReferenceType { get; set; }
    public string? ReferenceId { get; set; }
    public DateTimeOffset? ScheduledAvailableAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SupplierWalletEntriesResult
{
    public List<SupplierWalletEntryResult> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
