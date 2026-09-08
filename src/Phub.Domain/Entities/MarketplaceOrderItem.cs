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
    public string? ChannelSku { get; set; }
    public string? SabrVariantSku { get; set; }
    public string? ProductName { get; set; }
    public int Quantity { get; set; }
    public string? CurrencyId { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? FullUnitPrice { get; set; }
    public decimal? GrossPrice { get; set; }
    public decimal? SaleFee { get; set; }
    public int ReservedQuantity { get; set; }
    public string MappingState { get; set; } = "UNMAPPED";
    public Guid? MappingSnapshotId { get; set; }
    public long? MappingSnapshotVersion { get; set; }
    public string? MappingResolutionReason { get; set; }
    public DateTimeOffset? MappingResolvedAt { get; set; }
    public string? RawJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MarketplaceOrder? MarketplaceOrder { get; set; }
}
