namespace Phub.Domain.Entities;

public sealed class MarketplaceOrderNumberSequence
{
    public int Id { get; set; }
    public long NextNumber { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
