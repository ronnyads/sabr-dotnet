namespace Sabr.Application.Models;

public sealed class ProductImageResult
{
    public Guid Id { get; set; }
    public string ProductSku { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int SortOrder { get; set; }
    public bool IsPrimary { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
