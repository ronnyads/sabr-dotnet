namespace Phub.Application.Models;

public sealed class CatalogVariantDto
{
    public string VariantSku { get; set; } = string.Empty;
    public string BaseSku { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string VariantName { get; set; } = string.Empty;
    public int AvailableStock { get; set; }
    public string? ThumbnailUrl { get; set; }
}
