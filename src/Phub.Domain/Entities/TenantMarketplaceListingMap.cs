using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

public sealed class TenantMarketplaceListingMap
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public MarketplaceProvider Provider { get; set; } = MarketplaceProvider.MercadoLivre;
    public Guid? IntegrationId { get; set; }
    public long SellerId { get; set; }
    public string MlItemId { get; set; } = string.Empty;
    public string? MlVariationId { get; set; }
    public string? UserProductId { get; set; }
    public string? ChannelSku { get; set; }
    public string SabrVariantSku { get; set; } = string.Empty;
    public long MappingVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
