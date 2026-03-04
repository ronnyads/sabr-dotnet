namespace Sabr.Domain.Entities;

public sealed class ProductCatalog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid CatalogId { get; set; }
    private string _productSku = string.Empty;
    public string ProductSku
    {
        get => _productSku;
        set => _productSku = global::Sabr.Domain.ValueObjects.Sku.Normalize(value);
    }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
