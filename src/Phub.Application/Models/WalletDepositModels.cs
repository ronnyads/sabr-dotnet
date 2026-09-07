namespace Phub.Application.Models;

public sealed class WalletDepositCreateRequest
{
    public long AmountCents { get; set; }
    public string? ClientNote { get; set; }
}

public sealed class WalletDepositReviewRequest
{
    public string? Note { get; set; }
}

public sealed class WalletDepositResult
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public long AmountCents { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ProofFileName { get; set; } = string.Empty;
    public string? ClientNote { get; set; }
    public string? ReviewNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid? LedgerEntryId { get; set; }
}

public sealed class WalletLedgerItemResult
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public long AmountCents { get; set; }
    public long BalanceAfterCents { get; set; }
    public string? ReferenceType { get; set; }
    public string? ReferenceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ClientWalletResult
{
    public long BalanceCents { get; set; }
    public long PendingDepositCents { get; set; }
    public IReadOnlyList<WalletDepositResult> Deposits { get; set; } = Array.Empty<WalletDepositResult>();
    public IReadOnlyList<WalletLedgerItemResult> Ledger { get; set; } = Array.Empty<WalletLedgerItemResult>();
}

public sealed record WalletProofResult(string FileName, string ContentType, byte[] Content);
