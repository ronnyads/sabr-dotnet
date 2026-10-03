namespace Phub.Application.Models;

public sealed class CatalogProductDto
{
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public long CatalogPriceCents { get; set; }
    public int AvailableStock { get; set; }
    public bool IsActive { get; set; }
    public string Brand { get; set; } = string.Empty;
    public string? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public int VariantCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsAddedToMyProducts { get; set; }
}

public sealed class CatalogProductPageDto
{
    public List<CatalogProductDto> Items { get; set; } = new();
    public int Total { get; set; }
    public int Skip { get; set; }
    public int Limit { get; set; }
    public CatalogProductFacetsDto Facets { get; set; } = new();
}

public sealed class CatalogProductFacetsDto
{
    public List<CatalogFacetOptionDto> Categories { get; set; } = new();
    public List<CatalogFacetOptionDto> Brands { get; set; } = new();
    public int InStockCount { get; set; }
    public int OutOfStockCount { get; set; }
    public int AddedCount { get; set; }
    public int NotAddedCount { get; set; }
}

public sealed class CatalogFacetOptionDto
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class CatalogProductDetailDto
{
    public string ProductId { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public string? Supplier { get; set; }
    public string? Description { get; set; }
    public string? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? Ncm { get; set; }
    public string? Cest { get; set; }
    public string? FiscalOrigin { get; set; }
    public string? Ean { get; set; }
    public long CatalogPriceCents { get; set; }
    public int AvailableStock { get; set; }
    public bool IsAddedToMyProducts { get; set; }
    public decimal? WidthCm { get; set; }
    public decimal? HeightCm { get; set; }
    public decimal? LengthCm { get; set; }
    public decimal? WeightKg { get; set; }
    public bool RequiresAnatel { get; set; }
    public string? AnatelHomologationNumber { get; set; }
    public List<CatalogProductImageDto> Images { get; set; } = new();
    public List<CatalogProductVariantDto> Variants { get; set; } = new();
    public string FieldAuthority { get; set; } = "CATALOG";
    public CatalogProductQualityDto QualityStatus { get; set; } = new();
}

public sealed class CatalogProductQualityDto
{
    public bool FiscalComplete { get; set; }
    public bool DimensionsComplete { get; set; }
    public bool GtinComplete { get; set; }
    public bool ImagesComplete { get; set; }
    public List<string> MissingFields { get; set; } = new();
}

public sealed class CatalogProductImageDto
{
    public string Id { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int Position { get; set; }
    public bool IsPrimary { get; set; }
}

public sealed class CatalogProductVariantDto
{
    public string VariantId { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int AvailableStock { get; set; }
    public long CatalogPriceCents { get; set; }
}
