namespace Phub.Application.Models;

public sealed class AdminProductPricingUpdateRequest
{
    public long CostPriceCents { get; set; }
    public long CatalogPriceCents { get; set; }
    public string? Reason { get; set; }
}
