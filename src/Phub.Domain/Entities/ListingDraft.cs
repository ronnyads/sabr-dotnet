using Phub.Domain.Enums;
using Phub.Domain.ValueObjects;

namespace Phub.Domain.Entities;

public sealed class ListingDraft
{
    public Guid DraftId { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public MarketplaceProvider Provider { get; set; } = MarketplaceProvider.MercadoLivre;
    public Guid IntegrationId { get; set; }
    public long SellerId { get; set; }

    private string _baseProductSku = string.Empty;
    public string BaseProductSku
    {
        get => _baseProductSku;
        set => _baseProductSku = Sku.Normalize(value);
    }

    private string _sabrVariantSku = string.Empty;
    public string SabrVariantSku
    {
        get => _sabrVariantSku;
        set => _sabrVariantSku = Sku.Normalize(value);
    }

    public string? CategoryId { get; set; }
    public string? ListingTypeId { get; set; }
    public long? PriceCents { get; set; }
    public string CurrencyId { get; set; } = "BRL";
    public ListingDraftStatus Status { get; set; } = ListingDraftStatus.Draft;
    public string ProviderDraftJson { get; set; } = "{}";
    public string? PublishedItemId { get; set; }
    public string? PublishedVariationId { get; set; }
    public string? PublishedPermalink { get; set; }
    public string? PublishedApiUrl { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public string? LastErrorRawJson { get; set; }

    // ── Marketplace-specific fields ────────────────────────────────────────
    // ML: Garantia (sale_terms)
    public string? WarrantyType { get; set; }  // "Garantia do fabricante" | "Garantia do vendedor"
    public string? WarrantyTime { get; set; }  // "90 dias" | "6 meses" | "12 meses" | "24 meses"

    // ML: Frete
    public bool FreeShipping { get; set; } = false;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
