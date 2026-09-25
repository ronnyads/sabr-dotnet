using System;

namespace Phub.Application.Models;

public sealed class AdminProductResult
{
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public string? Ncm { get; set; }
    public string? Ean { get; set; }
    public string? Description { get; set; }
    public string? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? CategoryPath { get; set; }
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
    public string CatalogCostStatus { get; set; } = string.Empty;
    public string CatalogPriceOrigin { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public IReadOnlyCollection<ProductImageResult> Images { get; set; } = Array.Empty<ProductImageResult>();
    public IReadOnlyCollection<AdminProductListingLinkResult> ListingLinks { get; set; } = Array.Empty<AdminProductListingLinkResult>();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AdminProductListingLinkResult
{
    public Guid ClientId { get; set; }
    public Guid? IntegrationId { get; set; }
    public long SellerId { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public string? VariationId { get; set; }
    public string InternalSku { get; set; } = string.Empty;
    public long MappingVersion { get; set; }
}
