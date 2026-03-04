namespace Sabr.Application.Models;

public sealed class CatalogProductDto
{
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public long CatalogPriceCents { get; set; }
    public bool IsActive { get; set; }
}
