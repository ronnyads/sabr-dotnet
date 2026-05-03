using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

public sealed class MarketplaceOrderItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MarketplaceOrderId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public MarketplaceProvider Provider { get; set; } = MarketplaceProvider.MercadoLivre;
    public long SellerId { get; set; }
    public string MlItemId { get; set; } = string.Empty;
    public string? MlVariationId { get; set; }
    public string? SabrVariantSku { get; set; }
    public int Quantity { get; set; }
    public int ReservedQuantity { get; set; }
    public string MappingState { get; set; } = "UNMAPPED";
    public string? RawJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MarketplaceOrder? MarketplaceOrder { get; set; }
}
