using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

public sealed class SupplierWalletEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SupplierId { get; set; }
    public Guid? OrderId { get; set; }
    public WalletEntryType Type { get; set; }
    public long AmountCents { get; set; }
    public long BalanceAfterCents { get; set; }
    public SupplierWalletEntryStatus Status { get; set; } = SupplierWalletEntryStatus.Pending;
    public string? ReferenceType { get; set; }
    public string? ReferenceId { get; set; }
    public DateTimeOffset? ScheduledAvailableAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
