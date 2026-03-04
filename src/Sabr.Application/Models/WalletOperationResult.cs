namespace Sabr.Application.Models;

public sealed class WalletOperationResult
{
    public string Status { get; set; } = "Approved";
    public long BalanceAfterCents { get; set; }
    public Guid LedgerId { get; set; }
    public Guid RequestId { get; set; }
}
