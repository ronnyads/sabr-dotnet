using Sabr.Domain.Enums;

namespace Sabr.Domain.Entities;

public sealed class StockReservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public string SabrVariantSku { get; set; } = string.Empty;
    public Guid MarketplaceOrderId { get; set; }
    public Guid MarketplaceOrderItemId { get; set; }
    public int Quantity { get; set; }
    public StockReservationStatus Status { get; set; } = StockReservationStatus.Reserved;
    public DateTimeOffset ReservedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
