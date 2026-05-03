namespace Phub.Application.Models;

public sealed class WalletDebitRequest
{
    public Guid ClientId { get; set; }
    public long AmountCents { get; set; }
    public string? ReferenceType { get; set; }
    public string? ReferenceId { get; set; }
    public bool AllowNegative { get; set; } = false;
}
