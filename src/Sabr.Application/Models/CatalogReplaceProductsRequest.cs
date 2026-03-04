namespace Sabr.Application.Models;

public sealed class CatalogReplaceProductsRequest
{
    public List<string> ProductSkus { get; set; } = new();
}
