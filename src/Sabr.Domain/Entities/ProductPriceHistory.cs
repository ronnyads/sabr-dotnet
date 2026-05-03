namespace Phub.Domain.Entities;

public sealed class ProductPriceHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? TenantId { get; set; }
    private string _productSku = string.Empty;
    public string ProductSku
    {
        get => _productSku;
        set => _productSku = global::Phub.Domain.ValueObjects.Sku.Normalize(value);
    }
    public long OldCostPriceCents { get; set; }
    public long NewCostPriceCents { get; set; }
    public long OldCatalogPriceCents { get; set; }
    public long NewCatalogPriceCents { get; set; }
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid ChangedByUserId { get; set; }
    public string? Reason { get; set; }
}
