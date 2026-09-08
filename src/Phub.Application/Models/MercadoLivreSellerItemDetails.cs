namespace Phub.Application.Models;

public sealed class MercadoLivreSellerItemDetails
{
    public string ItemId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? SellerSku { get; set; }
    public string? Brand { get; set; }
    public string? Ean { get; set; }
    public string? ThumbnailUrl { get; set; }
    public decimal Price { get; set; }
    public string Status { get; set; } = string.Empty;
    public IReadOnlyList<MercadoLivreSellerVariationDetails> Variations { get; set; } = Array.Empty<MercadoLivreSellerVariationDetails>();
}

public sealed class MercadoLivreSellerVariationDetails
{
    public string VariationId { get; set; } = string.Empty;
    public string? SellerSku { get; set; }
}
