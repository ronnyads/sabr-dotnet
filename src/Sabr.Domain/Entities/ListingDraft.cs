using Sabr.Domain.Enums;
using Sabr.Domain.ValueObjects;

namespace Sabr.Domain.Entities;

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
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
