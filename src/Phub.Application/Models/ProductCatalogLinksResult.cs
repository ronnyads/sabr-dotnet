namespace Phub.Application.Models;

public sealed class ProductCatalogLinksResult
{
    public string ProductSku { get; set; } = string.Empty;
    public IReadOnlyCollection<Guid> CatalogIds { get; set; } = Array.Empty<Guid>();
}
