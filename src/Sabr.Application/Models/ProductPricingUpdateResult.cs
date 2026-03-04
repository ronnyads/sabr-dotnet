namespace Sabr.Application.Models;

public sealed class ProductPricingUpdateResult
{
    public string Sku { get; set; } = string.Empty;
    public long CostPriceCents { get; set; }
    public long CatalogPriceCents { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
