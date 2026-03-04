namespace Sabr.Application.Models;

public sealed class AdminProductVariantResult
{
    public string VariantSku { get; set; } = string.Empty;
    public string BaseSku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long CostPriceCents { get; set; }
    public long CatalogPriceCents { get; set; }
    public int PhysicalStock { get; set; }
    public int ReservedStock { get; set; }
    public int AvailableStock { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
