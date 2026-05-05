using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

public sealed class SupplierProduct
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    public string? TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public string? Ncm { get; set; }
    public string? Ean { get; set; }
    public Guid? CategoryId { get; set; }
    public long CostPriceCents { get; set; }
    public decimal PlatformMarginPercent { get; set; }
    public SupplierProductStatus Status { get; set; } = SupplierProductStatus.Draft;
    public string? AdminNotes { get; set; }
    public string? LinkedProductSku { get; set; }
    public string? Images { get; set; }
    public decimal? WidthCm { get; set; }
    public decimal? HeightCm { get; set; }
    public decimal? LengthCm { get; set; }
    public decimal? WeightKg { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? ApprovedByPlatformUserId { get; set; }
}
