namespace Phub.Application.Models;

public sealed class WalletCreditRequest
{
    public Guid ClientId { get; set; }
    public long AmountCents { get; set; }
    public string? ReferenceType { get; set; }
    public string? ReferenceId { get; set; }
}
