namespace Phub.Domain.Entities;

public sealed class SupplierWalletAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    public long PendingBalanceCents { get; set; }
    public long BlockedBalanceCents { get; set; }
    public long AvailableBalanceCents { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
