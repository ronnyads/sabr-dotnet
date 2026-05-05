using Phub.Domain.Enums;

namespace Phub.Application.Models;

public sealed class MarketplaceCatalogOptionDto
{
    public string BaseSku { get; set; } = string.Empty;
    public string VariantSku { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string VariantName { get; set; } = string.Empty;
    public int AvailableStock { get; set; }
    public string? ThumbnailUrl { get; set; }
    public bool IsDefaultVariant { get; set; }
}

public sealed class MarketplaceMappingListItemDto
{
    public Guid Id { get; set; }
    public MarketplaceProvider Provider { get; set; }
    public Guid? IntegrationId { get; set; }
    public string? SellerId { get; set; }
    public string ExternalItemId { get; set; } = string.Empty;
    public string? ExternalVariationId { get; set; }
    public string SabrVariantSku { get; set; } = string.Empty;
    public string? BaseSku { get; set; }
    public string? ProductName { get; set; }
    public string? VariantName { get; set; }
    public string? ChannelSku { get; set; }
    public string Action { get; set; } = "loaded";
    public int OrdersAffected { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class MarketplaceUnmappedItemDto
{
    public string MappingKey { get; set; } = string.Empty;
    public MarketplaceProvider Provider { get; set; }
    public Guid? IntegrationId { get; set; }
    public string? SellerId { get; set; }
    public string ExternalItemId { get; set; } = string.Empty;
    public string? ExternalVariationId { get; set; }
    public string? ChannelSku { get; set; }
    public string? ProductName { get; set; }
    public string? VariantName { get; set; }
    public string MappingReason { get; set; } = MarketplaceMappingReasonCodes.UnmappedUnknownChannelSku;
    public int OrdersAffected { get; set; }
    public int TotalUnits { get; set; }
    public DateTimeOffset LatestImportedAt { get; set; }
}

public sealed class MarketplaceUpsertMappingRequest
{
    public MarketplaceProvider Provider { get; set; }
    public Guid? IntegrationId { get; set; }
    public string? SellerId { get; set; }
    public string ExternalItemId { get; set; } = string.Empty;
    public string? ExternalVariationId { get; set; }
    public string SelectedCatalogSku { get; set; } = string.Empty;
}

public sealed record MarketplaceItemResolutionResult(
    string? SabrVariantSku,
    string MappingState,
    string MappingReason,
    string? ChannelSku,
    string ResolutionSource);

public static class MarketplaceMappingReasonCodes
{
    public const string MappedByExactSku = "mapped_by_exact_sku";
    public const string MappedByListingMap = "mapped_by_listing_map";
    public const string UnmappedMissingChannelSku = "unmapped_missing_channel_sku";
    public const string UnmappedUnknownChannelSku = "unmapped_unknown_channel_sku";
    public const string UnmappedSkuNotAuthorized = "unmapped_sku_not_authorized";
    public const string UnmappedMappedSkuNotAuthorized = "unmapped_mapping_not_authorized";
    public const string UnmappedNoImportedItems = "unmapped_no_imported_items";
}
