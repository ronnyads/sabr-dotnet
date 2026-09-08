using Phub.Domain.Enums;

namespace Phub.Application.Models;

public enum MarketplaceListingModel
{
    LegacyItem = 1,
    UserProduct = 2
}

public sealed class MarketplaceListingIdentity
{
    public MarketplaceProvider Provider { get; set; }
    public Guid IntegrationId { get; set; }
    public long SellerId { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public string? VariationId { get; set; }
    public string? UserProductId { get; set; }
}

public sealed class ClientMarketplaceListing
{
    public Guid MappingId { get; set; }
    public long MappingVersion { get; set; }
    public MarketplaceListingIdentity Identity { get; set; } = new();
    public MarketplaceListingModel Model { get; set; }
    public string MasterSku { get; set; } = string.Empty;
    public string? ChannelSku { get; set; }
    public string Title { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int AvailableQuantity { get; set; }
    public int SoldQuantity { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Permalink { get; set; }
    public bool IsCatalogListing { get; set; }
}

public sealed class MarketplaceListingFieldCapability
{
    public bool Editable { get; set; }
    public string? ReasonCode { get; set; }
    public string? Reason { get; set; }
    public object? CurrentValue { get; set; }
    public IReadOnlyList<string> AllowedValues { get; set; } = Array.Empty<string>();
}

public sealed class MarketplaceListingCapabilities
{
    public Guid MappingId { get; set; }
    public long MappingVersion { get; set; }
    public MarketplaceListingModel Model { get; set; }
    public string EvaluationHash { get; set; } = string.Empty;
    public DateTimeOffset EvaluatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Dictionary<string, MarketplaceListingFieldCapability> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class MarketplaceListingChangeSet
{
    public string EvaluationHash { get; set; } = string.Empty;
    public long MappingVersion { get; set; }
    public string? Title { get; set; }
    public decimal? Price { get; set; }
    public string? Description { get; set; }
}

public sealed class MarketplaceListingWorkspace
{
    public ClientMarketplaceListing Listing { get; set; } = new();
    public MarketplaceListingCapabilities Capabilities { get; set; } = new();
}

public sealed class MarketplaceListingChangeDraft
{
    public Guid DraftId { get; set; }
    public Guid MappingId { get; set; }
    public string Status { get; set; } = "DRAFT";
    public MarketplaceListingChangeSet Changes { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class MercadoLivreListingUpdateRequest
{
    public string? Title { get; set; }
    public decimal? Price { get; set; }
    public string? Description { get; set; }
}

public sealed class MercadoLivreUserProductStock
{
    public long Version { get; set; }
    public List<MercadoLivreUserProductStockLocation> Locations { get; set; } = new();
}

public sealed class MercadoLivreUserProductStockLocation
{
    public string Type { get; set; } = string.Empty;
    public string? StoreId { get; set; }
    public string? NetworkNodeId { get; set; }
    public int Quantity { get; set; }
}
