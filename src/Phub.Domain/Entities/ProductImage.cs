namespace Phub.Domain.Entities;

public sealed class ProductImage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    private string _productSku = string.Empty;
    public string ProductSku
    {
        get => _productSku;
        set => _productSku = global::Phub.Domain.ValueObjects.Sku.Normalize(value);
    }

    public string Url { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int SortOrder { get; set; }
    public bool IsPrimary { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
