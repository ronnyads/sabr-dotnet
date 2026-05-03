namespace Phub.Domain.Entities;

public sealed class Product
{
    private string _sku = string.Empty;
    public string Sku
    {
        get => _sku;
        set => _sku = global::Phub.Domain.ValueObjects.Sku.Normalize(value);
    }
    public string Name { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public string? Ncm { get; set; }
    public string? Ean { get; set; }
    public string? Description { get; set; }
    public string? CategoryId { get; set; }
    public string? ThumbnailUrl { get; set; }
    public decimal? WidthCm { get; set; }
    public decimal? HeightCm { get; set; }
    public decimal? LengthCm { get; set; }
    public decimal? WeightKg { get; set; }
    public bool RequiresAnatel { get; set; }
    public string? AnatelHomologationNumber { get; set; }
    public Guid? AnatelDocumentId { get; set; }
    public long CostPriceCents { get; set; }
    public long CatalogPriceCents { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
