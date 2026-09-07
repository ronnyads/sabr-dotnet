using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

public sealed class WalletDepositRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public long AmountCents { get; set; }
    public WalletDepositMethod Method { get; set; } = WalletDepositMethod.BankTransfer;
    public WalletDepositStatus Status { get; set; } = WalletDepositStatus.Pending;
    public string ProofFileName { get; set; } = string.Empty;
    public string ProofContentType { get; set; } = string.Empty;
    public long ProofSizeBytes { get; set; }
    public string? ClientNote { get; set; }
    public string? ReviewNote { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid? LedgerEntryId { get; set; }
    public WalletDepositProof? Proof { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WalletDepositProof
{
    public Guid DepositRequestId { get; set; }
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public WalletDepositRequest DepositRequest { get; set; } = null!;
}
