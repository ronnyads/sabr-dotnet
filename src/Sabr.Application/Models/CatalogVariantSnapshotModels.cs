namespace Sabr.Application.Models;

public sealed class CatalogVariantSnapshotRequest
{
    public string VariantSku { get; set; } = string.Empty;
}

public sealed class CatalogVariantSnapshotResult
{
    public string VariantSku { get; set; } = string.Empty;
    public string BaseSku { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal CostPrice { get; set; }
    public decimal? CatalogPrice { get; set; }
    public string CurrencyId { get; set; } = "BRL";
    public int StockAvailable { get; set; }
    public string? ResolvedVariantSku { get; set; }
    public string StockSource { get; set; } = "FallbackZero";
    public string? Gtin { get; set; }
    public string? Ncm { get; set; }
    public string? Origin { get; set; }
    public string? Brand { get; set; }
    public string ListingTypeDefault { get; set; } = "gold_special";
    public string SiteId { get; set; } = "MLB";
    public List<CatalogVariantSnapshotImageResult> Images { get; set; } = new();
    public CatalogVariantSnapshotDimensionsResult Dimensions { get; set; } = new();
    public bool VariantBackfilled { get; set; }
    public List<CatalogVariantSnapshotIssueResult> QualityIssues { get; set; } = new();
}

public sealed class CatalogVariantSnapshotImageResult
{
    public string Url { get; set; } = string.Empty;
    public int Position { get; set; }
}

public sealed class CatalogVariantSnapshotDimensionsResult
{
    public decimal? WeightKg { get; set; }
    public decimal? HeightCm { get; set; }
    public decimal? WidthCm { get; set; }
    public decimal? LengthCm { get; set; }
}

public sealed class CatalogVariantSnapshotIssueResult
{
    public string Code { get; set; } = string.Empty;
    public string FieldPath { get; set; } = string.Empty;
    public string Severity { get; set; } = "warning";
    public string Message { get; set; } = string.Empty;
}
