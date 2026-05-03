using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

public sealed class WalletLedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public Guid? OrderId { get; set; }
    public WalletEntryType Type { get; set; }
    public long AmountCents { get; set; }
    public long BalanceAfterCents { get; set; }
    public Guid RequestId { get; set; }
    public string? ReferenceType { get; set; }
    public string? ReferenceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
