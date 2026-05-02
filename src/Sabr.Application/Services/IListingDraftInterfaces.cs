using Sabr.Application.Models;
using Sabr.Application.Validation;

namespace Sabr.Application.Services;

/// <summary>
/// Gerencia a estimativa de taxas do Mercado Livre.
/// Extraído de ListingDraftService (Fase 4 — decomposição / ISP).
/// </summary>
public interface IListingFeeService
{
    Task<ServiceResult<MarketplaceFeesEstimateResult>> EstimateFeesAsync(
        string tenantId,
        Guid clientId,
        MarketplaceFeesEstimateRequest request,
        CancellationToken cancellationToken = default,
        string? traceId = null);
}

/// <summary>
/// Gerencia atributos e sugestões de categorias de marketplace.
/// Extraído de ListingDraftService (Fase 4 — decomposição / ISP).
/// </summary>
public interface IListingCategoryService
{
    Task<ServiceResult<MarketplaceCategoryAttributesResult>> GetCategoryAttributesAsync(
        string tenantId,
        Guid clientId,
        MarketplaceCategoryAttributesRequest request,
        CancellationToken cancellationToken = default,
        string? traceId = null);

    /// <summary>SuggestCategoriesAsync — traceId não é usado pela impl. atual (mantida compatível).</summary>
    Task<ServiceResult<MarketplaceCategorySuggestResult>> SuggestCategoriesAsync(
        string tenantId,
        Guid clientId,
        MarketplaceCategorySuggestRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Gerencia validação e publicação de drafts de listing.
/// Extraído de ListingDraftService (Fase 4 — decomposição / ISP).
/// </summary>
public interface IListingPublishService
{
    /// <summary>ValidateDraftAsync — traceId não é usado pela impl. atual (mantida compatível).</summary>
    Task<ServiceResult<ListingDraftValidateResult>> ValidateDraftAsync(
        string tenantId,
        Guid clientId,
        ListingDraftValidateRequest request,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<ListingDraftPublishResult>> PublishAsync(
        string tenantId,
        Guid clientId,
        ListingDraftPublishRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Consulta o histórico de publicações no marketplace.
/// Extraído de ListingDraftService (Fase 4 — decomposição / ISP).
/// </summary>
public interface IListingQueryService
{
    /// <summary>QueryPublicationsAsync — traceId não é usado pela impl. atual (mantida compatível).</summary>
    Task<ServiceResult<ListingPublicationsQueryResult>> QueryPublicationsAsync(
        string tenantId,
        Guid clientId,
        ListingPublicationsQueryRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Gerencia upsert e consulta de drafts de listing.
/// Extraído de ListingDraftService (Fase 4 — decomposição / ISP).
/// </summary>
public interface IListingDraftCrudService
{
    Task<ServiceResult<ListingDraftResult>> UpsertAsync(
        string tenantId,
        Guid clientId,
        ListingDraftUpsertRequest request,
        CancellationToken cancellationToken = default,
        string? traceId = null);

    Task<ServiceResult<ListingDraftGetResult>> GetAsync(
        string tenantId,
        Guid clientId,
        ListingDraftGetRequest request,
        CancellationToken cancellationToken = default,
        string? traceId = null);
}
