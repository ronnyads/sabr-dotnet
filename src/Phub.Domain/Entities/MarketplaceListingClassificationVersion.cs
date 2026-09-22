using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

/// <summary>Append-only classification and external-cost history for a marketplace listing/variation.</summary>
public sealed class MarketplaceListingClassificationVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public MarketplaceProvider Provider { get; set; }
    public Guid? IntegrationId { get; set; }
    public long SellerId { get; set; }
    public string ExternalItemId { get; set; } = string.Empty;
    public string ExternalVariationKey { get; set; } = string.Empty;
    public string Classification { get; set; } = MarketplaceListingClassifications.Pending;
    public string? SupplierName { get; set; }
    public string? Reason { get; set; }
    public long? ExternalUnitCostCents { get; set; }
    public string? CurrencyId { get; set; }
    public DateTimeOffset EffectiveAt { get; set; }
    public long Version { get; set; }
    public bool IsCurrent { get; set; }
    public Guid? ActorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class MarketplaceListingClassifications
{
    public const string ExternalSupplier = "EXTERNAL_SUPPLIER";
    public const string Pending = "PENDING";
}
