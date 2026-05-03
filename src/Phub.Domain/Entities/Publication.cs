using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

public sealed class Publication
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    private string _productSku = string.Empty;
    public string ProductSku
    {
        get => _productSku;
        set => _productSku = global::Phub.Domain.ValueObjects.Sku.Normalize(value);
    }
    public PublicationStatus Status { get; set; } = PublicationStatus.Draft;
    public PricingMode PricingMode { get; set; } = PricingMode.CatalogPrice;
    public decimal? MarkupPercent { get; set; }
    public long? FixedPriceCents { get; set; }
    public long CostPriceCentsSnapshot { get; set; }
    public long CatalogPriceCentsSnapshot { get; set; }
    public long FinalPriceCentsSnapshot { get; set; }
    public DateTimeOffset PriceSnapshotTakenAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid CreatedByUserId { get; set; }
    public Guid UpdatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
