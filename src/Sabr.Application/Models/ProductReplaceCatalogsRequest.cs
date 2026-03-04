namespace Sabr.Application.Models;

public sealed class ProductReplaceCatalogsRequest
{
    public List<Guid> CatalogIds { get; set; } = new();
}
