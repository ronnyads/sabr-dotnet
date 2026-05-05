namespace Phub.Application.Models;

public sealed class SupplierProductUpsertRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Brand { get; set; }
    public string? Ncm { get; set; }
    public string? Ean { get; set; }
    public long CostPriceCents { get; set; }
    public string? Images { get; set; }
    public decimal? WidthCm { get; set; }
    public decimal? HeightCm { get; set; }
    public decimal? LengthCm { get; set; }
    public decimal? WeightKg { get; set; }
}

public sealed class SupplierProductResult
{
    public Guid Id { get; set; }
    public Guid SupplierId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public string? Ncm { get; set; }
    public string? Ean { get; set; }
    public long CostPriceCents { get; set; }
    public decimal PlatformMarginPercent { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? AdminNotes { get; set; }
    public string? LinkedProductSku { get; set; }
    public string? Images { get; set; }
    public decimal? WidthCm { get; set; }
    public decimal? HeightCm { get; set; }
    public decimal? LengthCm { get; set; }
    public decimal? WeightKg { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
}

public sealed class AdminApproveSupplierProductRequest
{
    public decimal MarginPercent { get; set; }
    public Guid CatalogId { get; set; }
    public string? Notes { get; set; }
}

public sealed class AdminRejectSupplierProductRequest
{
    public string Reason { get; set; } = string.Empty;
}

public sealed class AdminRequestAdjustmentRequest
{
    public string Notes { get; set; } = string.Empty;
}
