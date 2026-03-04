namespace Sabr.Application.Models;

public sealed class AdminProductVariantCreateRequest
{
    public string VariantSku { get; set; } = string.Empty;
    public string? Name { get; set; }
    public long? CostPriceCents { get; set; }
    public long? CatalogPriceCents { get; set; }
    public int? PhysicalStock { get; set; }
    public int? ReservedStock { get; set; }
    public bool? IsActive { get; set; }
}
