using System.Text.Json.Serialization;

namespace Sabr.Application.Models;

public sealed class ListingDraftUpsertRequest
{
    public Guid? DraftId { get; set; }
    public Guid? IntegrationId { get; set; }
    public string? Channel { get; set; }
    public string? SellerId { get; set; }
    public string? SiteId { get; set; }
    public string? SabrVariantSku { get; set; }
    public string? CategoryId { get; set; }
    public string? ListingTypeId { get; set; }
    public string? Condition { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public decimal? Price { get; set; }
    public string? CurrencyId { get; set; }
    public string? Gtin { get; set; }
    public string? EmptyGtinReason { get; set; }
    public string? Ncm { get; set; }
    public string? Origin { get; set; }
    public List<ListingDraftImageRequest>? Images { get; set; }
    public List<ListingDraftAttributeRequest>? Attributes { get; set; }
    public decimal? ProductCost { get; set; }
    public decimal? OperationalCost { get; set; }
    public string? PublishMode { get; set; }
    public List<string>? SelectedVariantSkus { get; set; }
    public List<string>? VariationAxes { get; set; }
    public List<ListingDraftVariationRequest>? Variations { get; set; }
    public List<string>? ClearFields { get; set; }

    // ── Marketplace-specific fields ────────────────────────────────────────
    // ML: Garantia (sale_terms)
    public string? WarrantyType { get; set; }  // "Garantia do fabricante" | "Garantia do vendedor"
    public string? WarrantyTime { get; set; }  // "90 dias" | "6 meses" | "12 meses" | "24 meses"

    // ML: Frete grátis
    public bool FreeShipping { get; set; } = false;
}

public sealed class ListingDraftVariationRequest
{
    public string SabrVariantSku { get; set; } = string.Empty;
    public decimal? Price { get; set; }
    public int? InitialQuantity { get; set; }
    public List<ListingDraftVariationAttributeRequest> Attributes { get; set; } = new();
    public List<string> PictureIds { get; set; } = new();
}

public sealed class ListingDraftVariationAttributeRequest
{
    public string Id { get; set; } = string.Empty;
    public string? ValueId { get; set; }
    public string? ValueName { get; set; }
}

public sealed class ListingDraftImageRequest
{
    public string Url { get; set; } = string.Empty;
    public int Position { get; set; }
}

public sealed class ListingDraftAttributeRequest
{
    public string Id { get; set; } = string.Empty;
    public string? ValueId { get; set; }
    public string? ValueName { get; set; }
}

public sealed class ListingDraftResult
{
    public Guid DraftId { get; set; }
    public Guid IntegrationId { get; set; }
    public string Channel { get; set; } = "mercadolivre";
    public string SellerId { get; set; } = string.Empty;
    public string? SiteId { get; set; }
    public string BaseProductSku { get; set; } = string.Empty;
    public string SabrVariantSku { get; set; } = string.Empty;
    public string? CategoryId { get; set; }
    public string? ListingTypeId { get; set; }
    public string? Condition { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public decimal? Price { get; set; }
    public string CurrencyId { get; set; } = "BRL";
    public string? Gtin { get; set; }
    public string? EmptyGtinReason { get; set; }
    public string? Ncm { get; set; }
    public string? Origin { get; set; }
    public List<ListingDraftImageRequest> Images { get; set; } = new();
    public List<ListingDraftAttributeRequest> Attributes { get; set; } = new();
    public string Status { get; set; } = "Draft";
    public string RowVersion { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public List<string> Warnings { get; set; } = new();
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public string? PublishedItemId { get; set; }
    public string? PublishedVariationId { get; set; }
    public string? PublishedPermalink { get; set; }
    public string? PublishedApiUrl { get; set; }
}

public sealed class ListingDraftGetRequest
{
    public string VariantSku { get; set; } = string.Empty;
    public string? Channel { get; set; }
    public string? SellerId { get; set; }
    public Guid? IntegrationId { get; set; }
}

public sealed class ListingDraftCandidateVariantResult
{
    public string BaseProductSku { get; set; } = string.Empty;
    public string SabrVariantSku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

public sealed class ListingDraftGetResult
{
    public ListingDraftResult? Draft { get; set; }
    public List<ListingDraftCandidateVariantResult> Candidates { get; set; } = new();
    public string? ResolvedVariantSku { get; set; }
    public int AvailableStock { get; set; }
    public string StockSource { get; set; } = "FallbackZero";
    public string? SuggestedCategoryId { get; set; }
    public string? SuggestedCategorySource { get; set; }
    public string? SuggestedCategoryPath { get; set; }
    public string CategoryResolutionStatus { get; set; } = "SelectionRequired";
    public string? CategoryResolutionReason { get; set; }
    public bool CategorySelectionRequired { get; set; }
    public bool CategoryLockAvailable { get; set; } = true;
    public List<CategorySuggestionOptionResult> CategorySuggestions { get; set; } = new();
}

public sealed class CategorySuggestionOptionResult
{
    public string CategoryId { get; set; } = string.Empty;
    public string? CategoryName { get; set; }
    public string? CategoryPathFromRoot { get; set; }
    public string Source { get; set; } = "mapping";
    public int Rank { get; set; }
}

public sealed class ListingDraftPublishRequest
{
    public Guid DraftId { get; set; }
    public string? RowVersion { get; set; }
}

public sealed class ListingDraftPublishResult
{
    public Guid DraftId { get; set; }
    public string Status { get; set; } = "Draft";
    public string RowVersion { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public string? PublishedItemId { get; set; }
    public string? PublishedVariationId { get; set; }
    public string? PublishedPermalink { get; set; }
    public string? PublishedApiUrl { get; set; }
    public int? EffectiveQuantity { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<ListingDraftPublishVariationResult> VariationResults { get; set; } = new();
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
}

public sealed class ListingDraftPublishVariationResult
{
    public string SabrVariantSku { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? VariationId { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
}

public sealed class ListingDraftValidateRequest
{
    public Guid DraftId { get; set; }
}

public sealed class ListingDraftValidateResult
{
    public Guid DraftId { get; set; }
    public bool IsValid { get; set; }
    public string Status { get; set; } = "Draft";
    public string RowVersion { get; set; } = string.Empty;
    public List<ListingDraftValidationIssueResult> Issues { get; set; } = new();
}

public sealed class ListingDraftValidationIssueResult
{
    public string FieldPath { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Severity { get; set; } = "error";
    public string Step { get; set; } = "review";
}

public sealed class ListingPublicationsQueryRequest
{
    public Guid? IntegrationId { get; set; }
    public string? Channel { get; set; }
    public List<string>? VariantSkus { get; set; }
    public string? SellerId { get; set; }
    public string? Status { get; set; }
    public string? Search { get; set; }
    public int Skip { get; set; } = 0;
    public int Limit { get; set; } = 20;
}

public sealed class ListingPublicationItemResult
{
    public Guid DraftId { get; set; }
    public Guid IntegrationId { get; set; }
    public string SellerId { get; set; } = string.Empty;
    public string BaseProductSku { get; set; } = string.Empty;
    public string SabrVariantSku { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft";
    public string? CategoryId { get; set; }
    public string? ListingTypeId { get; set; }
    public decimal? Price { get; set; }
    public string CurrencyId { get; set; } = "BRL";
    public string? PublishedItemId { get; set; }
    public string? PublishedVariationId { get; set; }
    public string? PublishedPermalink { get; set; }
    public string? PublishedApiUrl { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ListingPublicationsQueryResult
{
    public int Total { get; set; }
    public int Skip { get; set; }
    public int Limit { get; set; }
    public List<ListingPublicationItemResult> Items { get; set; } = new();
}

public sealed class MarketplaceFeesEstimateRequest
{
    public Guid? IntegrationId { get; set; }
    public string? Channel { get; set; }
    public string? SellerId { get; set; }
    public string? SiteId { get; set; }
    public string? CategoryId { get; set; }
    public string? ListingTypeId { get; set; }
    public decimal? Price { get; set; }
    public string? CurrencyId { get; set; }
    public decimal? ProductCost { get; set; }
    public decimal? OperationalCost { get; set; }
}

public sealed class MarketplaceFeesEstimateResult
{
    public Guid IntegrationId { get; set; }
    public string SellerId { get; set; } = string.Empty;
    public string CategoryId { get; set; } = string.Empty;
    public string ListingTypeId { get; set; } = string.Empty;
    public string CurrencyId { get; set; } = "BRL";
    public decimal Price { get; set; }
    public decimal SaleFee { get; set; }
    public decimal FixedFee { get; set; }
    public decimal TotalFees { get; set; }
    public decimal ProductCost { get; set; }
    public decimal OperationalCost { get; set; }
    public decimal EstimatedProfit { get; set; }
    public decimal? MarginPercent { get; set; }
    public string? Source { get; set; }
}

public sealed class MarketplaceCategoryAttributesRequest
{
    public Guid? IntegrationId { get; set; }
    public string? Channel { get; set; }
    public string? SellerId { get; set; }
    public string? SiteId { get; set; }
    public string? CategoryId { get; set; }
}

public sealed class MarketplaceCategoryAttributeResult
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Required { get; set; }
    public bool Conditional { get; set; }
    public bool IsVariation { get; set; }
    public string? ValueType { get; set; }
    public List<MarketplaceCategoryAttributeValueResult> Values { get; set; } = new();
    public Dictionary<string, string> Tags { get; set; } = new();
}

public sealed class MarketplaceCategoryAttributeValueResult
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class MarketplaceCategoryAttributesResult
{
    public string? CategoryName { get; set; }
    public string? CategoryPathFromRoot { get; set; }
    public bool AllowsVariations { get; set; }
    public int MaxVariationsAllowed { get; set; }
    public int MaxVariationAttributes { get; set; }
    public List<string> AllowedVariationAttributes { get; set; } = new();
    public List<string> AllowedAxes { get; set; } = new();
    public List<MarketplaceCategoryAttributeResult> RequiredAttributes { get; set; } = new();
    public List<MarketplaceCategoryAttributeResult> ConditionalAttributes { get; set; } = new();
    public List<MarketplaceCategoryAttributeResult> OptionalAttributes { get; set; } = new();
}

public sealed class MarketplaceCategorySuggestRequest
{
    public string? Channel { get; set; }
    public string? SellerId { get; set; }
    public string? SiteId { get; set; }
    public string? Query { get; set; }
}

public enum SuggestDegradedReason
{
    ML_UNAVAILABLE,
    TIMEOUT,
    ML_AUTH_INVALID
}

public sealed class MarketplaceCategorySuggestResult
{
    public List<MarketplaceCategorySuggestItemResult> Items { get; set; } = new();
    public bool Degraded { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SuggestDegradedReason? Reason { get; set; }
    public string? TraceId { get; set; }
}

public sealed class MarketplaceCategorySuggestItemResult
{
    public string CategoryId { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string? DomainId { get; set; }
    public string? DomainName { get; set; }
    public string? Source { get; set; }
    public decimal? Score { get; set; }
    public string? PathFromRoot { get; set; }
}

public sealed class MercadoLivreFeeEstimateRequest
{
    public string CategoryId { get; set; } = string.Empty;
    public string ListingTypeId { get; set; } = "gold_special";
    public decimal Price { get; set; }
    public string CurrencyId { get; set; } = "BRL";
}

public sealed class MercadoLivreFeeEstimateResponse
{
    public decimal SaleFeeAmount { get; set; }
    public decimal FixedFeeAmount { get; set; }
    public decimal TotalFeeAmount { get; set; }
    public string RawJson { get; set; } = "{}";
}
