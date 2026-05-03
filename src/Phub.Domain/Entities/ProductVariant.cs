namespace Phub.Domain.Entities;

public sealed class ProductVariant
{
    private string _variantSku = string.Empty;
    public string VariantSku
    {
        get => _variantSku;
        set => _variantSku = global::Phub.Domain.ValueObjects.Sku.Normalize(value);
    }

    private string _baseSku = string.Empty;
    public string BaseSku
    {
        get => _baseSku;
        set => _baseSku = global::Phub.Domain.ValueObjects.Sku.Normalize(value);
    }

    public string Name { get; set; } = string.Empty;
    public long CostPriceCents { get; set; }
    public long CatalogPriceCents { get; set; }
    public int PhysicalStock { get; set; }
    public int ReservedStock { get; set; }
    public int AvailableStock { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
