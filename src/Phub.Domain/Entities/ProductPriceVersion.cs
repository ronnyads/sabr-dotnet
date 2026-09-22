namespace Phub.Domain.Entities;

public sealed class ProductPriceVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProductSku { get; set; } = string.Empty;
    public string? VariantSku { get; set; }
    public string PricingMode { get; set; } = ProductPricingModes.Inherited;
    public long CostPriceCents { get; set; }
    public long CatalogPriceCents { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
    public long Version { get; set; }
    public string ChangeType { get; set; } = ProductPriceChangeTypes.Change;
    public Guid ChangedByUserId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class ProductPricingModes
{
    public const string Inherited = "INHERITED";
    public const string Override = "OVERRIDE";
}

public static class ProductPriceChangeTypes
{
    public const string Change = "CHANGE";
    public const string Correction = "CORRECTION";
}
