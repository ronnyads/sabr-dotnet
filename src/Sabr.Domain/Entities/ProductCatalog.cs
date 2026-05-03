namespace Phub.Domain.Entities;

public sealed class ProductCatalog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CatalogId { get; set; }
    private string _productSku = string.Empty;
    public string ProductSku
    {
        get => _productSku;
        set => _productSku = global::Phub.Domain.ValueObjects.Sku.Normalize(value);
    }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
