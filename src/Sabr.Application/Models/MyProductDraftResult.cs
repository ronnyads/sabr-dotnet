using Sabr.Domain.Enums;

namespace Sabr.Application.Models;

public sealed class MyProductDraftResult
{
    public Guid Id { get; set; }
    public string ProductSku { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public PublicationStatus Status { get; set; }
    public PricingMode PricingMode { get; set; }
    public decimal? MarkupPercent { get; set; }
    public long? FixedPriceCents { get; set; }
    public long CatalogPriceCentsSnapshot { get; set; }
    public long FinalPriceCentsSnapshot { get; set; }
    public DateTimeOffset PriceSnapshotTakenAt { get; set; }
    public string? Description { get; set; }
    public List<MyProductImageResult> Images { get; set; } = new();
    public string? Gtin { get; set; }
    public string? Ncm { get; set; }
    public string? Origin { get; set; }
    public decimal? PurchaseCost { get; set; }
    public decimal? CatalogPrice { get; set; }
    public string RowVersion { get; set; } = "0";
    public bool HasProductVariant { get; set; }
    public string VariantStatus { get; set; } = "Missing";
    public string? ResolvedVariantSku { get; set; }
    public int? AvailableStock { get; set; }
    public string StockSource { get; set; } = "FallbackZero";
    public string MlOverallStatus { get; set; } = "None";
    public int MlPublishedCount { get; set; }
    public int MlDraftCount { get; set; }
    public int MlErrorCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class MyProductImageResult
{
    public string Url { get; set; } = string.Empty;
    public int Position { get; set; }
}
