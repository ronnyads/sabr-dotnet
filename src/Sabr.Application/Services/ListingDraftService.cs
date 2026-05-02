using System.Globalization;
using System.Net;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sabr.Application.Abstractions;
using Sabr.Application.Categories;
using Sabr.Application.Models;
using Sabr.Application.Options;
using Sabr.Application.Stock;
using Sabr.Application.Validation;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;
using Sabr.Domain.ValueObjects;

namespace Sabr.Application.Services;

public sealed class ListingDraftService :
    IListingDraftCrudService,
    IListingFeeService,
    IListingCategoryService,
    IListingPublishService,
    IListingQueryService
{
    private const int MaxPublishQuantity = 999999;
    private const string PublishModeSingleVariant = "SingleVariant";
    private const string PublishModeMultiVariation = "MultiVariation";
    private const string AxisValidationMetadataSeparator = "::";
    private const string MercadoLivreChannel = "mercadolivre";
    private const int MaxTitleLength = 60;
    private static readonly Meter PublishMeter = new("Sabr.ListingDraft.Publish", "1.0.0");
    private static readonly Counter<long> MlPublishInputInvalidCounter = PublishMeter.CreateCounter<long>("ml_publish_input_invalid_total");
    private static readonly Counter<long> MlPublishUnavailableCounter = PublishMeter.CreateCounter<long>("ml_publish_unavailable_total");
    private static readonly Counter<long> MlPublishAuthInvalidCounter = PublishMeter.CreateCounter<long>("ml_publish_auth_invalid_total");
    private static readonly Counter<long> MlCategoryInvalidCounter = PublishMeter.CreateCounter<long>("ml_category_invalid_total");
    private static readonly Regex SiteIdRegex = new("^ML[A-Z]$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> QueryStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "None",
        "Draft",
        "Valid",
        "Publishing",
        "Published",
        "Paused",
        "UnderReview",
        "Error"
    };
    private static readonly JsonSerializerOptions ProviderDraftJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IAppDbContext _dbContext;
    private readonly MercadoLivreOAuthService _oauthService;
    private readonly IMercadoLivreApiClient _mercadoLivreApiClient;
    private readonly StockAvailabilityService _stockAvailabilityService;
    private readonly MarketplaceAuditLogService _marketplaceAuditLogService;
    private readonly IMemoryCache _memoryCache;
    private readonly MercadoLivreOptions _mercadoLivreOptions;
    private readonly MarketplaceCategoryResolver _marketplaceCategoryResolver;
    private readonly ILogger<ListingDraftService> _logger;
    private readonly bool _isNpgsqlProvider;

    public ListingDraftService(
        IAppDbContext dbContext,
        MercadoLivreOAuthService oauthService,
        IMercadoLivreApiClient mercadoLivreApiClient,
        StockAvailabilityService stockAvailabilityService,
        MarketplaceAuditLogService marketplaceAuditLogService,
        IMemoryCache memoryCache,
        IOptions<MercadoLivreOptions> mercadoLivreOptions,
        MarketplaceCategoryResolver marketplaceCategoryResolver,
        ILogger<ListingDraftService> logger)
    {
        _dbContext = dbContext;
        _oauthService = oauthService;
        _mercadoLivreApiClient = mercadoLivreApiClient;
        _stockAvailabilityService = stockAvailabilityService;
        _marketplaceAuditLogService = marketplaceAuditLogService;
        _memoryCache = memoryCache;
        _mercadoLivreOptions = mercadoLivreOptions.Value;
        _marketplaceCategoryResolver = marketplaceCategoryResolver;
        _logger = logger;
        _isNpgsqlProvider = string.Equals(
            (_dbContext as DbContext)?.Database.ProviderName,
            "Npgsql.EntityFrameworkCore.PostgreSQL",
            StringComparison.Ordinal);
    }

    public async Task<ServiceResult<ListingDraftResult>> UpsertAsync(
        string tenantId,
        Guid clientId,
        ListingDraftUpsertRequest request,
        CancellationToken cancellationToken = default,
        string? traceId = null)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty)
        {
            return Failure<ListingDraftResult>("context", "INVALID_CONTEXT");
        }

        if (!IsMercadoLivreChannel(request.Channel))
        {
            return Failure<ListingDraftResult>("channel", "CHANNEL_INVALID");
        }

        var clearSet = new HashSet<string>(
            request.ClearFields?.Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        ListingDraft? draft = null;
        TenantMarketplaceConnection? connection = null;
        var draftCreatedNow = false;

        if (request.DraftId.HasValue)
        {
            draft = await _dbContext.ListingDrafts.FirstOrDefaultAsync(
                item => item.DraftId == request.DraftId.Value
                        && item.TenantId == tenantId
                        && item.ClientId == clientId
                        && item.Provider == MarketplaceProvider.MercadoLivre,
                cancellationToken);
            if (draft == null)
            {
                return Failure<ListingDraftResult>("draftId", "DRAFT_NOT_FOUND");
            }

            connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
                item => item.Id == draft.IntegrationId
                        && item.TenantId == tenantId
                        && item.ClientId == clientId
                        && item.Provider == MarketplaceProvider.MercadoLivre,
                cancellationToken);
            if (connection == null)
            {
                return Failure<ListingDraftResult>("integrationId", "INTEGRATION_REQUIRED");
            }
        }
        else
        {
            if (!request.IntegrationId.HasValue || request.IntegrationId == Guid.Empty)
            {
                return Failure<ListingDraftResult>("integrationId", "INTEGRATION_REQUIRED");
            }

            if (string.IsNullOrWhiteSpace(request.SabrVariantSku))
            {
                return Failure<ListingDraftResult>("sabrVariantSku", "SABR_VARIANT_SKU_REQUIRED");
            }

            var normalizedVariantSku = Sku.Normalize(request.SabrVariantSku);
            connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
                item => item.Id == request.IntegrationId.Value
                        && item.TenantId == tenantId
                        && item.ClientId == clientId
                        && item.Provider == MarketplaceProvider.MercadoLivre,
                cancellationToken);
            if (connection == null)
            {
                return Failure<ListingDraftResult>("integrationId", "INTEGRATION_REQUIRED");
            }

            if (!TryValidateSellerCompatibility(request.SellerId, connection.SellerId, out var sellerError))
            {
                return Failure<ListingDraftResult>("sellerId", sellerError!);
            }

            draft = await _dbContext.ListingDrafts.FirstOrDefaultAsync(
                item => item.TenantId == tenantId
                        && item.ClientId == clientId
                        && item.Provider == MarketplaceProvider.MercadoLivre
                        && item.IntegrationId == connection.Id
                        && item.SabrVariantSku == normalizedVariantSku,
                cancellationToken);

            if (draft == null)
            {
                var variant = await GetOrBackfillVariantAsync(
                    normalizedVariantSku,
                    tenantId,
                    clientId,
                    cancellationToken);
                if (variant == null)
                {
                    return Failure<ListingDraftResult>("sabrVariantSku", "SKU_NOT_FOUND");
                }

                draft = new ListingDraft
                {
                    DraftId = request.DraftId.GetValueOrDefault(Guid.NewGuid()),
                    TenantId = tenantId,
                    ClientId = clientId,
                    Provider = MarketplaceProvider.MercadoLivre,
                    IntegrationId = connection.Id,
                    SellerId = connection.SellerId,
                    BaseProductSku = variant.BaseSku,
                    SabrVariantSku = variant.VariantSku,
                    CurrencyId = "BRL",
                    Status = ListingDraftStatus.Draft,
                    ProviderDraftJson = "{}",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                _dbContext.ListingDrafts.Add(draft);
                draftCreatedNow = true;
            }
        }

        if (connection == null)
        {
            connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
                item => item.Id == draft!.IntegrationId
                        && item.TenantId == tenantId
                        && item.ClientId == clientId
                        && item.Provider == MarketplaceProvider.MercadoLivre,
                cancellationToken);
            if (connection == null)
            {
                return Failure<ListingDraftResult>("integrationId", "INTEGRATION_REQUIRED");
            }
        }

        if (!TryValidateSellerCompatibility(request.SellerId, connection.SellerId, out var sellerMismatch))
        {
            return Failure<ListingDraftResult>("sellerId", sellerMismatch!);
        }

        if (request.IntegrationId.HasValue && request.IntegrationId != Guid.Empty && request.IntegrationId != draft!.IntegrationId)
        {
            return Failure<ListingDraftResult>("integrationId", "INVALID_SELLER_INTEGRATION");
        }

        var providerDraft = ReadProviderDraftData(draft!.ProviderDraftJson);
        var previousDraftCategoryId = draft.CategoryId;
        var materialBefore = BuildMaterialFingerprint(draft, providerDraft);
        ApplyPatch(draft, request, clearSet, connection.SellerId);
        ApplyProviderPatch(providerDraft, request, clearSet);
        MarketplaceCategoryResolutionResult? categoryResolution = null;
        var categoryAutofilledNow = false;
        if (!clearSet.Contains("categoryId"))
        {
            string? resolverAccessToken = null;
            try
            {
                resolverAccessToken = await _oauthService.GetValidAccessTokenAsync(connection, cancellationToken);
            }
            catch (Exception ex) when (IsMlAuthInvalidException(ex) || IsUnauthorizedOrForbidden(ex) || IsMlTransientException(ex))
            {
                _logger.LogInformation(
                    ex,
                    "category_resolve_upsert_token_unavailable tenantId={TenantId} clientId={ClientId} draftId={DraftId} integrationId={IntegrationId} sellerId={SellerId} traceId={TraceId}",
                    tenantId,
                    clientId,
                    draft.DraftId,
                    connection.Id,
                    connection.SellerId,
                    traceId ?? string.Empty);
            }

            categoryResolution = await _marketplaceCategoryResolver.ResolveAsync(
                new MarketplaceCategoryResolverRequest
                {
                    TenantId = tenantId,
                    ClientId = clientId,
                    BaseProductSku = draft.BaseProductSku,
                    SiteId = providerDraft.SiteId ?? "MLB",
                    Query = providerDraft.Title,
                    DraftCategoryId = draft.CategoryId,
                    DraftSiteId = providerDraft.SiteId,
                    AccessToken = resolverAccessToken,
                    TraceId = traceId
                },
                cancellationToken);

            if (draftCreatedNow &&
                string.IsNullOrWhiteSpace(draft.CategoryId) &&
                categoryResolution.ResolutionStatus == CategoryResolutionStatus.Ready &&
                !string.IsNullOrWhiteSpace(categoryResolution.SuggestedCategoryId))
            {
                draft.CategoryId = categoryResolution.SuggestedCategoryId;
                categoryAutofilledNow = true;
            }

            try
            {
                await UpsertCategoryLockFromDraftAsync(
                    tenantId,
                    clientId,
                    draft.BaseProductSku,
                    providerDraft.SiteId ?? "MLB",
                    previousDraftCategoryId,
                    draft.CategoryId,
                    request.CategoryId,
                    categoryAutofilledNow,
                    categoryResolution,
                    traceId,
                    resolverAccessToken,
                    cancellationToken);
            }
            catch (Exception ex) when (IsCategoryLockStorageUnavailable(ex))
            {
                _logger.LogWarning(
                    ex,
                    "category_lock_unavailable tenantId={TenantId} clientId={ClientId} baseSku={BaseSku} siteId={SiteId} action={Action} traceId={TraceId}",
                    tenantId,
                    clientId,
                    draft.BaseProductSku,
                    providerDraft.SiteId ?? "MLB",
                    "write",
                    traceId ?? string.Empty);
            }
        }

        var materialAfter = BuildMaterialFingerprint(draft, providerDraft);
        var materialChanged = !string.Equals(materialBefore, materialAfter, StringComparison.Ordinal);
        if (materialChanged && (draft.Status == ListingDraftStatus.Valid || draft.Status == ListingDraftStatus.Error))
        {
            draft.Status = ListingDraftStatus.Draft;
        }
        draft.ProviderDraftJson = BuildProviderDraftJson(draft, request, providerDraft);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(
                ex,
                "listing_draft_upsert_db_concurrency_conflict tenantId={TenantId} clientId={ClientId} draftId={DraftId} integrationId={IntegrationId} variantSku={VariantSku} traceId={TraceId}",
                tenantId,
                clientId,
                draft.DraftId,
                draft.IntegrationId,
                draft.SabrVariantSku,
                traceId ?? string.Empty);
            return Failure<ListingDraftResult>("draftId", "DRAFT_CONCURRENCY_CONFLICT");
        }
        catch (DbUpdateException ex) when (IsDraftNaturalKeyUniqueViolation(ex))
        {
            var recovered = await TryRecoverUpsertNaturalKeyRaceAsync(
                tenantId,
                clientId,
                draft.IntegrationId,
                draft.SabrVariantSku,
                draft,
                traceId,
                cancellationToken);
            if (recovered != null)
            {
                return recovered;
            }

            throw;
        }
        catch (DbUpdateException ex) when (IsCategoryLockStorageUnavailable(ex))
        {
            var efDbContext = _dbContext as DbContext;
            if (efDbContext == null || !DetachTrackedCategoryLockEntries(efDbContext))
            {
                throw;
            }

            _logger.LogWarning(
                ex,
                "category_lock_unavailable tenantId={TenantId} clientId={ClientId} baseSku={BaseSku} siteId={SiteId} action={Action} traceId={TraceId}",
                tenantId,
                clientId,
                draft.BaseProductSku,
                providerDraft.SiteId ?? "MLB",
                "write_retry_without_lock",
                traceId ?? string.Empty);
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException retryEx)
            {
                _logger.LogWarning(
                    retryEx,
                    "listing_draft_upsert_db_concurrency_conflict tenantId={TenantId} clientId={ClientId} draftId={DraftId} integrationId={IntegrationId} variantSku={VariantSku} traceId={TraceId}",
                    tenantId,
                    clientId,
                    draft.DraftId,
                    draft.IntegrationId,
                    draft.SabrVariantSku,
                    traceId ?? string.Empty);
                return Failure<ListingDraftResult>("draftId", "DRAFT_CONCURRENCY_CONFLICT");
            }
            catch (DbUpdateException retryEx) when (IsDraftNaturalKeyUniqueViolation(retryEx))
            {
                var recovered = await TryRecoverUpsertNaturalKeyRaceAsync(
                    tenantId,
                    clientId,
                    draft.IntegrationId,
                    draft.SabrVariantSku,
                    draft,
                    traceId,
                    cancellationToken);
                if (recovered != null)
                {
                    return recovered;
                }

                throw;
            }
        }

        var rowVersion = GetRowVersion(draft);
        return ServiceResult<ListingDraftResult>.Success(MapListingDraftResult(draft, rowVersion));
    }

    public async Task<ServiceResult<ListingDraftGetResult>> GetAsync(
        string tenantId,
        Guid clientId,
        ListingDraftGetRequest request,
        CancellationToken cancellationToken = default,
        string? traceId = null)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty)
        {
            return Failure<ListingDraftGetResult>("context", "INVALID_CONTEXT");
        }

        if (!IsMercadoLivreChannel(request.Channel))
        {
            return Failure<ListingDraftGetResult>("channel", "CHANNEL_INVALID");
        }

        if (string.IsNullOrWhiteSpace(request.VariantSku))
        {
            return Failure<ListingDraftGetResult>("variantSku", "VARIANT_SKU_REQUIRED");
        }

        var normalizedSku = Sku.Normalize(request.VariantSku);
        var context = await ResolveVariantContextForGetAsync(
            normalizedSku,
            tenantId,
            clientId,
            cancellationToken);
        if (context == null)
        {
            return Failure<ListingDraftGetResult>("variantSku", "SKU_NOT_FOUND");
        }

        var resolved = VariantStockResolver.Resolve(normalizedSku, context.Variants);
        var baseSku = context.BaseSku;
        var resolvedVariantSku = resolved.ResolvedVariantSku;

        var candidates = await _dbContext.ProductVariants.AsNoTracking()
            .Where(item => item.BaseSku == baseSku)
            .OrderBy(item => item.VariantSku)
            .Select(item => new ListingDraftCandidateVariantResult
            {
                BaseProductSku = item.BaseSku,
                SabrVariantSku = item.VariantSku,
                Name = item.Name,
                IsActive = item.IsActive
            })
            .ToListAsync(cancellationToken);

        var draftQuery = _dbContext.ListingDrafts.AsNoTracking()
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == MarketplaceProvider.MercadoLivre
                           && item.BaseProductSku == baseSku);

        if (!string.IsNullOrWhiteSpace(request.SellerId))
        {
            if (!MercadoLivreSellerIdParser.TryParseRequired(request.SellerId, out var parsedSeller))
            {
                return Failure<ListingDraftGetResult>("sellerId", "SELLER_INVALID");
            }

            draftQuery = draftQuery.Where(item => item.SellerId == parsedSeller);
        }

        if (request.IntegrationId.HasValue && request.IntegrationId != Guid.Empty)
        {
            if (!string.IsNullOrWhiteSpace(request.SellerId))
            {
                var connection = await _dbContext.TenantMarketplaceConnections.AsNoTracking().FirstOrDefaultAsync(
                    item => item.Id == request.IntegrationId.Value
                            && item.TenantId == tenantId
                            && item.ClientId == clientId
                            && item.Provider == MarketplaceProvider.MercadoLivre,
                    cancellationToken);
                if (connection == null)
                {
                    return Failure<ListingDraftGetResult>("integrationId", "INTEGRATION_REQUIRED");
                }

                if (!MercadoLivreSellerIdParser.TryParseRequired(request.SellerId, out var parsedSeller)
                    || parsedSeller != connection.SellerId)
                {
                    return Failure<ListingDraftGetResult>("sellerId", "INVALID_SELLER_INTEGRATION");
                }
            }

            draftQuery = draftQuery.Where(item => item.IntegrationId == request.IntegrationId.Value);
        }

        var selectedDraft = await draftQuery
            .Where(item => resolvedVariantSku != null && item.SabrVariantSku == resolvedVariantSku)
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.DraftId)
            .FirstOrDefaultAsync(cancellationToken);

        if (selectedDraft == null)
        {
            selectedDraft = await draftQuery
                .OrderByDescending(item => item.UpdatedAt)
                .ThenByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.DraftId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var mappedDraft = selectedDraft == null ? null : MapListingDraftResult(selectedDraft, GetRowVersion(selectedDraft));
        var categorySiteId = mappedDraft?.SiteId ?? "MLB";
        var categoryQuery = mappedDraft?.Title ?? candidates.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Name))?.Name ?? baseSku;

        TenantMarketplaceConnection? categoryConnection = null;
        if (request.IntegrationId.HasValue && request.IntegrationId.Value != Guid.Empty)
        {
            categoryConnection = await _dbContext.TenantMarketplaceConnections.AsNoTracking()
                .FirstOrDefaultAsync(
                    item => item.Id == request.IntegrationId.Value
                            && item.TenantId == tenantId
                            && item.ClientId == clientId
                            && item.Provider == MarketplaceProvider.MercadoLivre,
                    cancellationToken);
        }

        if (categoryConnection == null && selectedDraft != null)
        {
            categoryConnection = await _dbContext.TenantMarketplaceConnections.AsNoTracking()
                .FirstOrDefaultAsync(
                    item => item.Id == selectedDraft.IntegrationId
                            && item.TenantId == tenantId
                            && item.ClientId == clientId
                            && item.Provider == MarketplaceProvider.MercadoLivre,
                    cancellationToken);
        }

        if (categoryConnection == null && !string.IsNullOrWhiteSpace(request.SellerId) &&
            MercadoLivreSellerIdParser.TryParseRequired(request.SellerId, out var sellerFromRequest))
        {
            categoryConnection = await _dbContext.TenantMarketplaceConnections.AsNoTracking()
                .Where(item => item.TenantId == tenantId
                               && item.ClientId == clientId
                               && item.Provider == MarketplaceProvider.MercadoLivre
                               && item.SellerId == sellerFromRequest)
                .OrderByDescending(item => item.UpdatedAt)
                .ThenByDescending(item => item.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (categoryConnection == null)
        {
            categoryConnection = await _dbContext.TenantMarketplaceConnections.AsNoTracking()
                .Where(item => item.TenantId == tenantId
                               && item.ClientId == clientId
                               && item.Provider == MarketplaceProvider.MercadoLivre)
                .OrderByDescending(item => item.UpdatedAt)
                .ThenByDescending(item => item.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        string? categoryAccessToken = null;
        if (categoryConnection != null)
        {
            try
            {
                categoryAccessToken = await _oauthService.GetValidAccessTokenAsync(categoryConnection, cancellationToken);
            }
            catch (Exception ex) when (IsMlAuthInvalidException(ex) || IsUnauthorizedOrForbidden(ex) || IsMlTransientException(ex))
            {
                _logger.LogInformation(
                    ex,
                    "category_resolve_get_token_unavailable tenantId={TenantId} clientId={ClientId} baseSku={BaseSku} integrationId={IntegrationId} sellerId={SellerId} traceId={TraceId}",
                    tenantId,
                    clientId,
                    baseSku,
                    categoryConnection.Id,
                    categoryConnection.SellerId,
                    traceId ?? string.Empty);
            }
        }

        var categoryResolution = await _marketplaceCategoryResolver.ResolveAsync(
            new MarketplaceCategoryResolverRequest
            {
                TenantId = tenantId,
                ClientId = clientId,
                BaseProductSku = baseSku,
                SiteId = categorySiteId,
                Query = categoryQuery,
                DraftCategoryId = mappedDraft?.CategoryId,
                DraftSiteId = mappedDraft?.SiteId,
                AccessToken = categoryAccessToken,
                TraceId = traceId
            },
            cancellationToken);

        if (categoryResolution.LockRequiresReview)
        {
            try
            {
                await MarkCategoryLockReviewRequiredAsync(
                    tenantId,
                    clientId,
                    baseSku,
                    categoryResolution.SiteId,
                    cancellationToken);
            }
            catch (Exception ex) when (IsCategoryLockStorageUnavailable(ex))
            {
                _logger.LogWarning(
                    ex,
                    "category_lock_unavailable tenantId={TenantId} clientId={ClientId} baseSku={BaseSku} siteId={SiteId} action={Action} traceId={TraceId}",
                    tenantId,
                    clientId,
                    baseSku,
                    categoryResolution.SiteId,
                    "mark_review_required",
                    traceId ?? string.Empty);
            }
        }

        _logger.LogInformation(
            "Stock resolved. productSku={ProductSku} requestedVariantSku={Requested} resolvedVariantSku={Resolved} source={Source} stock={Stock}",
            baseSku,
            normalizedSku,
            resolved.ResolvedVariantSku,
            resolved.StockSource,
            resolved.AvailableStock);

        return ServiceResult<ListingDraftGetResult>.Success(new ListingDraftGetResult
        {
            Draft = mappedDraft,
            Candidates = candidates,
            ResolvedVariantSku = resolved.ResolvedVariantSku,
            AvailableStock = resolved.AvailableStock,
            StockSource = resolved.StockSource.ToString(),
            SuggestedCategoryId = categoryResolution.SuggestedCategoryId,
            SuggestedCategorySource = categoryResolution.SuggestedCategorySource,
            SuggestedCategoryPath = categoryResolution.SuggestedCategoryPath,
            CategoryResolutionStatus = categoryResolution.ResolutionStatus.ToString(),
            CategoryResolutionReason = categoryResolution.CategoryResolutionReason,
            CategorySelectionRequired = categoryResolution.CategorySelectionRequired,
            CategoryLockAvailable = categoryResolution.CategoryLockAvailable,
            CategorySuggestions = categoryResolution.Suggestions
                .Select(item => new CategorySuggestionOptionResult
                {
                    CategoryId = item.CategoryId,
                    CategoryName = item.CategoryName,
                    CategoryPathFromRoot = item.CategoryPathFromRoot,
                    Source = item.Source,
                    Rank = item.Rank
                })
                .ToList()
        });
    }

    public async Task<ServiceResult<MarketplaceFeesEstimateResult>> EstimateFeesAsync(
        string tenantId,
        Guid clientId,
        MarketplaceFeesEstimateRequest request,
        CancellationToken cancellationToken = default,
        string? traceId = null)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty)
        {
            return Failure<MarketplaceFeesEstimateResult>("context", "INVALID_CONTEXT");
        }

        if (!IsMercadoLivreChannel(request.Channel))
        {
            return Failure<MarketplaceFeesEstimateResult>("channel", "CHANNEL_INVALID");
        }

        if (!request.IntegrationId.HasValue || request.IntegrationId.Value == Guid.Empty)
        {
            return Failure<MarketplaceFeesEstimateResult>("integrationId", "INTEGRATION_REQUIRED");
        }

        if (string.IsNullOrWhiteSpace(request.CategoryId))
        {
            return Failure<MarketplaceFeesEstimateResult>("categoryId", "CATEGORY_REQUIRED");
        }

        var listingTypeId = ListingDraftHelpers.NormalizeListingTypeId(request.ListingTypeId);
        if (!ListingDraftHelpers.IsValidListingType(listingTypeId))
        {
            return Failure<MarketplaceFeesEstimateResult>("listingTypeId", "LISTING_TYPE_INVALID");
        }

        if (!request.Price.HasValue || request.Price.Value <= 0)
        {
            return Failure<MarketplaceFeesEstimateResult>("price", "PRICE_INVALID");
        }

        var currency = ListingDraftHelpers.NormalizeCurrency(request.CurrencyId);
        if (!string.Equals(currency, "BRL", StringComparison.Ordinal))
        {
            return Failure<MarketplaceFeesEstimateResult>("currencyId", "CURRENCY_NOT_SUPPORTED");
        }

        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.Id == request.IntegrationId.Value
                    && item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.MercadoLivre,
            cancellationToken);
        if (connection == null)
        {
            return Failure<MarketplaceFeesEstimateResult>("integrationId", "INTEGRATION_REQUIRED");
        }

        if (!string.IsNullOrWhiteSpace(request.SellerId))
        {
            if (!MercadoLivreSellerIdParser.TryParseRequired(request.SellerId, out var parsedSellerId))
            {
                return Failure<MarketplaceFeesEstimateResult>("sellerId", "SELLER_INVALID");
            }

            if (parsedSellerId != connection.SellerId)
            {
                return Failure<MarketplaceFeesEstimateResult>("sellerId", "INVALID_SELLER_INTEGRATION");
            }
        }

        if (!TryNormalizeAndValidateCategoryId(
                request.SiteId,
                request.CategoryId,
                out var normalizedSiteId,
                out var normalizedCategoryId))
        {
            _logger.LogWarning(
                "category_id_invalid_local tenantId={TenantId} clientId={ClientId} integrationId={IntegrationId} sellerId={SellerId} sellerIdRaw={SellerIdRaw} endpoint={Endpoint} siteId={SiteId} categoryIdRaw={CategoryIdRaw} categoryIdNormalized={CategoryIdNormalized} traceId={TraceId}",
                tenantId,
                clientId,
                request.IntegrationId,
                connection.SellerId,
                request.SellerId,
                "fees/estimate",
                normalizedSiteId,
                request.CategoryId,
                normalizedCategoryId,
                traceId ?? string.Empty);
            MlCategoryInvalidCounter.Add(
                1,
                new KeyValuePair<string, object?>("endpoint", "fees/estimate"),
                new KeyValuePair<string, object?>("siteId", normalizedSiteId));
            return Failure<MarketplaceFeesEstimateResult>("categoryId", "ML_FEES_INPUT_INVALID");
        }

        var normalizedPrice = ListingDraftHelpers.ToDecimal(ListingDraftHelpers.ToCents(request.Price.Value));
        var productCost = request.ProductCost.HasValue
            ? ListingDraftHelpers.ToDecimal(ListingDraftHelpers.ToCents(request.ProductCost.Value))
            : 0m;
        var operationalCost = request.OperationalCost.HasValue
            ? ListingDraftHelpers.ToDecimal(ListingDraftHelpers.ToCents(request.OperationalCost.Value))
            : 0m;
        string accessToken;
        try
        {
            accessToken = await _oauthService.GetValidAccessTokenAsync(connection, cancellationToken);
        }
        catch (Exception ex) when (IsMlAuthInvalidException(ex))
        {
            return Failure<MarketplaceFeesEstimateResult>("sellerId", "ML_AUTH_INVALID");
        }

        MercadoLivreFeeEstimateResponse feeResponse;
        try
        {
            feeResponse = await _mercadoLivreApiClient.EstimateFeesAsync(
                new MercadoLivreFeeEstimateRequest
                {
                    CategoryId = normalizedCategoryId,
                    ListingTypeId = listingTypeId,
                    Price = normalizedPrice,
                    CurrencyId = currency
                },
                normalizedSiteId,
                accessToken,
                cancellationToken);
        }
        catch (Exception ex) when (IsUnauthorizedOrForbidden(ex))
        {
            try
            {
                accessToken = await _oauthService.GetValidAccessTokenAsync(connection, cancellationToken, forceRefresh: true);
            }
            catch (Exception refreshEx) when (IsMlAuthInvalidException(refreshEx))
            {
                return Failure<MarketplaceFeesEstimateResult>("sellerId", "ML_AUTH_INVALID");
            }

            try
            {
                feeResponse = await _mercadoLivreApiClient.EstimateFeesAsync(
                    new MercadoLivreFeeEstimateRequest
                    {
                        CategoryId = normalizedCategoryId,
                        ListingTypeId = listingTypeId,
                        Price = normalizedPrice,
                        CurrencyId = currency
                    },
                    normalizedSiteId,
                    accessToken,
                    cancellationToken);
            }
            catch (Exception retryAuthEx) when (IsUnauthorizedOrForbidden(retryAuthEx))
            {
                return Failure<MarketplaceFeesEstimateResult>("sellerId", "ML_AUTH_INVALID");
            }
            catch (Exception retryEx) when (IsMlFeesInputInvalidException(retryEx))
            {
                return Failure<MarketplaceFeesEstimateResult>("categoryId", "ML_FEES_INPUT_INVALID");
            }
            catch (Exception retryEx) when (IsMlTransientException(retryEx))
            {
                _logger.LogWarning(
                    retryEx,
                    "fees_estimate_unavailable tenantId={TenantId} clientId={ClientId} integrationId={IntegrationId} sellerId={SellerId} categoryId={CategoryId} listingTypeId={ListingTypeId} endpoint={Endpoint} fallbackUsed={FallbackUsed}",
                    tenantId,
                    clientId,
                    connection.Id,
                    connection.SellerId,
                    normalizedCategoryId,
                    listingTypeId,
                    "fees/estimate",
                    false);
                return Failure<MarketplaceFeesEstimateResult>("integrationId", "ML_UNAVAILABLE");
            }
        }
        catch (Exception ex) when (IsMlFeesInputInvalidException(ex))
        {
            return Failure<MarketplaceFeesEstimateResult>("categoryId", "ML_FEES_INPUT_INVALID");
        }
        catch (Exception ex) when (IsMlTransientException(ex))
        {
            try
            {
                feeResponse = await _mercadoLivreApiClient.EstimateFeesAsync(
                    new MercadoLivreFeeEstimateRequest
                    {
                        CategoryId = normalizedCategoryId,
                        ListingTypeId = listingTypeId,
                        Price = normalizedPrice,
                        CurrencyId = currency
                    },
                    normalizedSiteId,
                    accessToken,
                    cancellationToken);
            }
            catch (Exception retryAuthEx) when (IsUnauthorizedOrForbidden(retryAuthEx))
            {
                return Failure<MarketplaceFeesEstimateResult>("sellerId", "ML_AUTH_INVALID");
            }
            catch (Exception retryEx) when (IsMlFeesInputInvalidException(retryEx))
            {
                return Failure<MarketplaceFeesEstimateResult>("categoryId", "ML_FEES_INPUT_INVALID");
            }
            catch (Exception retryEx) when (IsMlTransientException(retryEx))
            {
                _logger.LogWarning(
                    retryEx,
                    "fees_estimate_unavailable tenantId={TenantId} clientId={ClientId} integrationId={IntegrationId} sellerId={SellerId} categoryId={CategoryId} listingTypeId={ListingTypeId} endpoint={Endpoint} fallbackUsed={FallbackUsed}",
                    tenantId,
                    clientId,
                    connection.Id,
                    connection.SellerId,
                    normalizedCategoryId,
                    listingTypeId,
                    "fees/estimate",
                    false);
                return Failure<MarketplaceFeesEstimateResult>("integrationId", "ML_UNAVAILABLE");
            }
        }

        var estimate = BuildFeesEstimateResult(
            connection.Id,
            connection.SellerId,
            normalizedCategoryId,
            listingTypeId,
            currency,
            normalizedPrice,
            productCost,
            operationalCost,
            feeResponse,
            "ml-api");
        return ServiceResult<MarketplaceFeesEstimateResult>.Success(estimate);
    }

    public async Task<ServiceResult<MarketplaceCategoryAttributesResult>> GetCategoryAttributesAsync(
        string tenantId,
        Guid clientId,
        MarketplaceCategoryAttributesRequest request,
        CancellationToken cancellationToken = default,
        string? traceId = null)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty)
        {
            return Failure<MarketplaceCategoryAttributesResult>("context", "INVALID_CONTEXT");
        }

        if (!IsMercadoLivreChannel(request.Channel))
        {
            return Failure<MarketplaceCategoryAttributesResult>("channel", "CHANNEL_INVALID");
        }

        if (!request.IntegrationId.HasValue || request.IntegrationId.Value == Guid.Empty)
        {
            return Failure<MarketplaceCategoryAttributesResult>("integrationId", "INTEGRATION_REQUIRED");
        }

        if (string.IsNullOrWhiteSpace(request.CategoryId))
        {
            return Failure<MarketplaceCategoryAttributesResult>("categoryId", "CATEGORY_REQUIRED");
        }

        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.Id == request.IntegrationId.Value
                    && item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.MercadoLivre,
            cancellationToken);
        if (connection == null)
        {
            return Failure<MarketplaceCategoryAttributesResult>("integrationId", "INTEGRATION_REQUIRED");
        }

        if (!string.IsNullOrWhiteSpace(request.SellerId))
        {
            if (!MercadoLivreSellerIdParser.TryParseRequired(request.SellerId, out var parsedSeller))
            {
                return Failure<MarketplaceCategoryAttributesResult>("sellerId", "SELLER_INVALID");
            }

            if (parsedSeller != connection.SellerId)
            {
                return Failure<MarketplaceCategoryAttributesResult>("sellerId", "INVALID_SELLER_INTEGRATION");
            }
        }

        if (!TryNormalizeAndValidateCategoryId(
                request.SiteId,
                request.CategoryId,
                out var normalizedSiteId,
                out var normalizedCategoryId))
        {
            _logger.LogWarning(
                "category_id_invalid_local tenantId={TenantId} clientId={ClientId} integrationId={IntegrationId} sellerId={SellerId} sellerIdRaw={SellerIdRaw} endpoint={Endpoint} siteId={SiteId} categoryIdRaw={CategoryIdRaw} categoryIdNormalized={CategoryIdNormalized} traceId={TraceId}",
                tenantId,
                clientId,
                request.IntegrationId,
                connection.SellerId,
                request.SellerId,
                "categories/attributes",
                normalizedSiteId,
                request.CategoryId,
                normalizedCategoryId,
                traceId ?? string.Empty);
            MlCategoryInvalidCounter.Add(
                1,
                new KeyValuePair<string, object?>("endpoint", "categories/attributes"),
                new KeyValuePair<string, object?>("siteId", normalizedSiteId));
            return Failure<MarketplaceCategoryAttributesResult>("categoryId", "ML_CATEGORY_INVALID");
        }

        string accessToken;
        try
        {
            accessToken = await _oauthService.GetValidAccessTokenAsync(connection, cancellationToken);
        }
        catch (Exception ex) when (IsMlAuthInvalidException(ex))
        {
            return Failure<MarketplaceCategoryAttributesResult>("sellerId", "ML_AUTH_INVALID");
        }

        MercadoLivreCategoryCapabilityResponse capabilities;
        IReadOnlyList<MercadoLivreCategoryAttributeResponse> attributes;
        try
        {
            capabilities = await _mercadoLivreApiClient.GetCategoryCapabilitiesAsync(normalizedCategoryId, accessToken, cancellationToken);
            attributes = await _mercadoLivreApiClient.GetCategoryAttributesAsync(normalizedCategoryId, accessToken, cancellationToken);
        }
        catch (Exception ex) when (IsUnauthorizedOrForbidden(ex))
        {
            try
            {
                accessToken = await _oauthService.GetValidAccessTokenAsync(connection, cancellationToken, forceRefresh: true);
            }
            catch (Exception refreshEx) when (IsMlAuthInvalidException(refreshEx))
            {
                return Failure<MarketplaceCategoryAttributesResult>("sellerId", "ML_AUTH_INVALID");
            }

            try
            {
                capabilities = await _mercadoLivreApiClient.GetCategoryCapabilitiesAsync(normalizedCategoryId, accessToken, cancellationToken);
                attributes = await _mercadoLivreApiClient.GetCategoryAttributesAsync(normalizedCategoryId, accessToken, cancellationToken);
            }
            catch (Exception retryAuthEx) when (IsUnauthorizedOrForbidden(retryAuthEx))
            {
                return Failure<MarketplaceCategoryAttributesResult>("sellerId", "ML_AUTH_INVALID");
            }
            catch (Exception retryEx) when (IsMlCategoryInvalidException(retryEx))
            {
                return Failure<MarketplaceCategoryAttributesResult>("categoryId", "ML_CATEGORY_INVALID");
            }
            catch (Exception retryEx) when (IsMlTransientException(retryEx))
            {
                _logger.LogWarning(
                    retryEx,
                    "category_attributes_unavailable tenantId={TenantId} clientId={ClientId} integrationId={IntegrationId} sellerId={SellerId} categoryId={CategoryId} endpoint={Endpoint}",
                    tenantId,
                    clientId,
                    connection.Id,
                    connection.SellerId,
                    normalizedCategoryId,
                    "categories/attributes");
                return Failure<MarketplaceCategoryAttributesResult>("categoryId", "ML_UNAVAILABLE");
            }
        }
        catch (Exception ex) when (IsMlCategoryInvalidException(ex))
        {
            return Failure<MarketplaceCategoryAttributesResult>("categoryId", "ML_CATEGORY_INVALID");
        }
        catch (Exception ex) when (IsMlTransientException(ex))
        {
            _logger.LogWarning(
                ex,
                "category_attributes_unavailable tenantId={TenantId} clientId={ClientId} integrationId={IntegrationId} sellerId={SellerId} categoryId={CategoryId} endpoint={Endpoint}",
                tenantId,
                clientId,
                connection.Id,
                connection.SellerId,
                normalizedCategoryId,
                "categories/attributes");
            return Failure<MarketplaceCategoryAttributesResult>("categoryId", "ML_UNAVAILABLE");
        }

        var required = attributes
            .Where(item => item.Required)
            .Select(item => new MarketplaceCategoryAttributeResult
            {
                Id = item.Id,
                Name = item.Name,
                Required = true,
                Conditional = item.Conditional,
                IsVariation = item.IsVariation,
                ValueType = item.ValueType,
                Values = item.Values
                    .Select(value => new MarketplaceCategoryAttributeValueResult
                    {
                        Id = value.Id,
                        Name = value.Name
                    })
                    .ToList(),
                Tags = new Dictionary<string, string>(item.Tags, StringComparer.OrdinalIgnoreCase)
            })
            .ToList();
        var conditional = attributes
            .Where(item => item.Conditional && !item.Required)
            .Select(item => new MarketplaceCategoryAttributeResult
            {
                Id = item.Id,
                Name = item.Name,
                Required = false,
                Conditional = true,
                IsVariation = item.IsVariation,
                ValueType = item.ValueType,
                Values = item.Values
                    .Select(value => new MarketplaceCategoryAttributeValueResult
                    {
                        Id = value.Id,
                        Name = value.Name
                    })
                    .ToList(),
                Tags = new Dictionary<string, string>(item.Tags, StringComparer.OrdinalIgnoreCase)
            })
            .ToList();
        var optional = attributes
            .Where(item => !item.Required && !item.Conditional)
            .Select(item => new MarketplaceCategoryAttributeResult
            {
                Id = item.Id,
                Name = item.Name,
                Required = false,
                Conditional = false,
                IsVariation = item.IsVariation,
                ValueType = item.ValueType,
                Values = item.Values
                    .Select(value => new MarketplaceCategoryAttributeValueResult
                    {
                        Id = value.Id,
                        Name = value.Name
                    })
                    .ToList(),
                Tags = new Dictionary<string, string>(item.Tags, StringComparer.OrdinalIgnoreCase)
            })
            .ToList();

        var allowedVariationAttributes = capabilities.AllowedVariationAttributes.Count > 0
            ? capabilities.AllowedVariationAttributes
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : attributes
                .Where(item => item.IsVariation)
                .Select(item => item.Id)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        var allowedAxes = attributes
            .Where(item => item.IsVariation &&
                           (allowedVariationAttributes.Count == 0 ||
                            allowedVariationAttributes.Contains(item.Id, StringComparer.OrdinalIgnoreCase)))
            .Select(item => string.IsNullOrWhiteSpace(item.Name) ? item.Id : item.Name)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ServiceResult<MarketplaceCategoryAttributesResult>.Success(new MarketplaceCategoryAttributesResult
        {
            CategoryName = string.IsNullOrWhiteSpace(capabilities.CategoryName) ? null : capabilities.CategoryName.Trim(),
            CategoryPathFromRoot = string.IsNullOrWhiteSpace(capabilities.CategoryPathFromRoot) ? null : capabilities.CategoryPathFromRoot.Trim(),
            AllowsVariations = capabilities.AllowsVariations,
            MaxVariationsAllowed = capabilities.MaxVariationsAllowed,
            MaxVariationAttributes = capabilities.MaxVariationAttributes,
            AllowedVariationAttributes = allowedVariationAttributes,
            AllowedAxes = allowedAxes,
            RequiredAttributes = required,
            ConditionalAttributes = conditional,
            OptionalAttributes = optional
        });
    }

    public async Task<ServiceResult<MarketplaceCategorySuggestResult>> SuggestCategoriesAsync(
        string tenantId,
        Guid clientId,
        MarketplaceCategorySuggestRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty)
        {
            return Failure<MarketplaceCategorySuggestResult>("context", "INVALID_CONTEXT");
        }

        if (!IsMercadoLivreChannel(request.Channel))
        {
            return Failure<MarketplaceCategorySuggestResult>("channel", "CHANNEL_INVALID");
        }

        if (!MercadoLivreSellerIdParser.TryParseRequired(request.SellerId, out var sellerId))
        {
            return Failure<MarketplaceCategorySuggestResult>("sellerId", "SELLER_INVALID");
        }

        var query = NormalizeSuggestQuery(request.Query);
        if (string.IsNullOrWhiteSpace(query))
        {
            return Failure<MarketplaceCategorySuggestResult>("query", "QUERY_REQUIRED");
        }

        if (query.Length < 3)
        {
            return ServiceResult<MarketplaceCategorySuggestResult>.Success(
                new MarketplaceCategorySuggestResult
                {
                    Items = new List<MarketplaceCategorySuggestItemResult>(),
                    Degraded = false,
                    Reason = null,
                    TraceId = null
                });
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ServiceResult<MarketplaceCategorySuggestResult>.Success(
                await CreateDegradedSuggestResultAsync(
                    SuggestDegradedReason.TIMEOUT,
                    tenantId,
                    clientId,
                    safeSiteId: string.IsNullOrWhiteSpace(request.SiteId) ? "MLB" : request.SiteId.Trim().ToUpperInvariant(),
                    query,
                    cancellationToken));
        }

        var safeSiteId = string.IsNullOrWhiteSpace(request.SiteId)
            ? "MLB"
            : request.SiteId.Trim().ToUpperInvariant();
        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.MercadoLivre
                    && item.SellerId == sellerId,
            cancellationToken);
        if (connection == null)
        {
            return Failure<MarketplaceCategorySuggestResult>("sellerId", "INVALID_SELLER_INTEGRATION");
        }

        var cacheKey = $"ml:cat-suggest:{tenantId}:{clientId:N}:{connection.Id:N}:{safeSiteId}:{query.ToUpperInvariant()}";
        if (_memoryCache.TryGetValue(cacheKey, out MarketplaceCategorySuggestResult? cachedResult) && cachedResult != null)
        {
            return ServiceResult<MarketplaceCategorySuggestResult>.Success(cachedResult);
        }

        IReadOnlyList<MercadoLivreDomainDiscoverySuggestion> suggestions;
        string accessToken;
        try
        {
            accessToken = await _oauthService.GetValidAccessTokenAsync(connection, cancellationToken);
        }
        catch (Exception ex) when (IsMlAuthInvalidException(ex))
        {
            return ServiceResult<MarketplaceCategorySuggestResult>.Success(
                await CreateDegradedSuggestResultAsync(
                    SuggestDegradedReason.ML_AUTH_INVALID,
                    tenantId,
                    clientId,
                    safeSiteId,
                    query,
                    cancellationToken));
        }

        try
        {
            suggestions = await _mercadoLivreApiClient.SuggestCategoriesByDomainDiscoveryAsync(
                safeSiteId,
                query,
                accessToken,
                cancellationToken);
        }
        catch (HttpRequestException ex) when (IsUnauthorizedOrForbidden(ex.StatusCode))
        {
            try
            {
                accessToken = await _oauthService.GetValidAccessTokenAsync(connection, cancellationToken, forceRefresh: true);
            }
            catch (Exception refreshEx) when (IsMlAuthInvalidException(refreshEx))
            {
                return ServiceResult<MarketplaceCategorySuggestResult>.Success(
                    await CreateDegradedSuggestResultAsync(
                        SuggestDegradedReason.ML_AUTH_INVALID,
                        tenantId,
                        clientId,
                        safeSiteId,
                        query,
                        cancellationToken));
            }

            try
            {
                suggestions = await _mercadoLivreApiClient.SuggestCategoriesByDomainDiscoveryAsync(
                    safeSiteId,
                    query,
                    accessToken,
                    cancellationToken);
            }
            catch (HttpRequestException retryAuthEx) when (IsUnauthorizedOrForbidden(retryAuthEx.StatusCode))
            {
                return ServiceResult<MarketplaceCategorySuggestResult>.Success(
                    await CreateDegradedSuggestResultAsync(
                        SuggestDegradedReason.ML_AUTH_INVALID,
                        tenantId,
                        clientId,
                        safeSiteId,
                        query,
                        cancellationToken));
            }
            catch (TaskCanceledException retryTimeoutEx)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug(
                        retryTimeoutEx,
                        "Category suggest cancelled by client token. tenantId={TenantId} clientId={ClientId} sellerId={SellerId}",
                        tenantId,
                        clientId,
                        sellerId);
                }
                else
                {
                    _logger.LogWarning(
                        retryTimeoutEx,
                        "Category suggest timeout after retry. tenantId={TenantId} clientId={ClientId} sellerId={SellerId}",
                        tenantId,
                        clientId,
                        sellerId);
                }

                return ServiceResult<MarketplaceCategorySuggestResult>.Success(
                    await CreateDegradedSuggestResultAsync(
                        SuggestDegradedReason.TIMEOUT,
                        tenantId,
                        clientId,
                        safeSiteId,
                        query,
                        cancellationToken));
            }
            catch (HttpRequestException retryHttpEx) when (IsSuggestTransientFailure(retryHttpEx.StatusCode))
            {
                _logger.LogWarning(
                    retryHttpEx,
                    "Category suggest degraded after retry due to transient ML error. tenantId={TenantId} clientId={ClientId} sellerId={SellerId}",
                    tenantId,
                    clientId,
                    sellerId);
                return ServiceResult<MarketplaceCategorySuggestResult>.Success(
                    await CreateDegradedSuggestResultAsync(
                        SuggestDegradedReason.ML_UNAVAILABLE,
                        tenantId,
                        clientId,
                        safeSiteId,
                        query,
                        cancellationToken));
            }
            catch (HttpRequestException retryBadRequestEx) when (retryBadRequestEx.StatusCode == HttpStatusCode.BadRequest)
            {
                _logger.LogWarning(
                    "ml_suggest_bad_request_ignored_after_retry query={Query} siteId={SiteId} tenantId={TenantId} clientId={ClientId} sellerId={SellerId}",
                    query,
                    safeSiteId,
                    tenantId,
                    clientId,
                    sellerId);
                return ServiceResult<MarketplaceCategorySuggestResult>.Success(
                    new MarketplaceCategorySuggestResult
                    {
                        Items = new List<MarketplaceCategorySuggestItemResult>(),
                        Degraded = false,
                        Reason = null,
                        TraceId = null
                    });
            }
            catch (InvalidOperationException retryCircuitEx) when (string.Equals(retryCircuitEx.Message, "ML_CIRCUIT_OPEN", StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    retryCircuitEx,
                    "Category suggest degraded after retry due to ML circuit open. tenantId={TenantId} clientId={ClientId} sellerId={SellerId}",
                    tenantId,
                    clientId,
                    sellerId);
                return ServiceResult<MarketplaceCategorySuggestResult>.Success(
                    await CreateDegradedSuggestResultAsync(
                        SuggestDegradedReason.ML_UNAVAILABLE,
                        tenantId,
                        clientId,
                        safeSiteId,
                        query,
                        cancellationToken));
            }
        }
        catch (TaskCanceledException timeoutEx)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(
                    timeoutEx,
                    "Category suggest cancelled by client token. tenantId={TenantId} clientId={ClientId} sellerId={SellerId}",
                    tenantId,
                    clientId,
                    sellerId);
            }
            else
            {
                _logger.LogWarning(
                    timeoutEx,
                    "Category suggest timeout. tenantId={TenantId} clientId={ClientId} sellerId={SellerId}",
                    tenantId,
                    clientId,
                    sellerId);
            }

            return ServiceResult<MarketplaceCategorySuggestResult>.Success(
                await CreateDegradedSuggestResultAsync(
                    SuggestDegradedReason.TIMEOUT,
                    tenantId,
                    clientId,
                    safeSiteId,
                    query,
                    cancellationToken));
        }
        catch (HttpRequestException httpEx) when (IsSuggestTransientFailure(httpEx.StatusCode))
        {
            _logger.LogWarning(
                httpEx,
                "Category suggest degraded due to transient ML error. tenantId={TenantId} clientId={ClientId} sellerId={SellerId}",
                tenantId,
                clientId,
                sellerId);
            return ServiceResult<MarketplaceCategorySuggestResult>.Success(
                await CreateDegradedSuggestResultAsync(
                    SuggestDegradedReason.ML_UNAVAILABLE,
                    tenantId,
                    clientId,
                    safeSiteId,
                    query,
                    cancellationToken));
        }
        catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.BadRequest)
        {
            _logger.LogWarning(
                "ml_suggest_bad_request_ignored query={Query} siteId={SiteId} tenantId={TenantId} clientId={ClientId} sellerId={SellerId}",
                query,
                safeSiteId,
                tenantId,
                clientId,
                sellerId);
            return ServiceResult<MarketplaceCategorySuggestResult>.Success(
                new MarketplaceCategorySuggestResult
                {
                    Items = new List<MarketplaceCategorySuggestItemResult>(),
                    Degraded = false,
                    Reason = null,
                    TraceId = null
                });
        }
        catch (InvalidOperationException circuitEx) when (string.Equals(circuitEx.Message, "ML_CIRCUIT_OPEN", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                circuitEx,
                "Category suggest degraded due to ML circuit open. tenantId={TenantId} clientId={ClientId} sellerId={SellerId}",
                tenantId,
                clientId,
                sellerId);
            return ServiceResult<MarketplaceCategorySuggestResult>.Success(
                await CreateDegradedSuggestResultAsync(
                    SuggestDegradedReason.ML_UNAVAILABLE,
                    tenantId,
                    clientId,
                    safeSiteId,
                    query,
                    cancellationToken));
        }

        var result = new MarketplaceCategorySuggestResult
        {
            Items = suggestions
                .Where(item => !string.IsNullOrWhiteSpace(item.CategoryId))
                .GroupBy(item => item.CategoryId.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select(item => new MarketplaceCategorySuggestItemResult
                {
                    CategoryId = item.CategoryId.Trim(),
                    CategoryName = item.CategoryName,
                    DomainId = item.DomainId,
                    DomainName = item.DomainName,
                    Source = "domain_discovery",
                    Score = item.Score,
                    PathFromRoot = item.PathFromRoot
                })
                .ToList(),
            Degraded = false,
            Reason = null,
            TraceId = null
        };

        if (result.Items.Count > 0)
        {
            _memoryCache.Set(cacheKey, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24)
            });
        }

        return ServiceResult<MarketplaceCategorySuggestResult>.Success(result);
    }

    public async Task<ServiceResult<ListingDraftValidateResult>> ValidateDraftAsync(
        string tenantId,
        Guid clientId,
        ListingDraftValidateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty)
        {
            return Failure<ListingDraftValidateResult>("context", "INVALID_CONTEXT");
        }

        if (request.DraftId == Guid.Empty)
        {
            return Failure<ListingDraftValidateResult>("draftId", "DRAFT_NOT_FOUND");
        }

        var draft = await _dbContext.ListingDrafts.FirstOrDefaultAsync(
            item => item.DraftId == request.DraftId
                    && item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.MercadoLivre,
            cancellationToken);
        if (draft == null)
        {
            return Failure<ListingDraftValidateResult>("draftId", "DRAFT_NOT_FOUND");
        }

        var validation = await ValidateDraftForPublishAsync(tenantId, clientId, draft, cancellationToken);
        if (!validation.Succeeded && ShouldReturnAsHttpFailure(validation.Errors))
        {
            return ServiceResult<ListingDraftValidateResult>.Failure(validation.Errors);
        }

        var issues = new List<ListingDraftValidationIssueResult>();
        if (!validation.Succeeded)
        {
            issues.AddRange(validation.Errors.Select(MapValidationIssue));
        }

        if (issues.Count == 0)
        {
            draft.Status = ListingDraftStatus.Valid;
            draft.UpdatedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        var rowVersion = GetRowVersion(draft);

        return ServiceResult<ListingDraftValidateResult>.Success(new ListingDraftValidateResult
        {
            DraftId = draft.DraftId,
            IsValid = issues.Count == 0,
            Status = draft.Status.ToString(),
            RowVersion = rowVersion,
            Issues = issues
        });
    }

    public async Task<ServiceResult<ListingDraftPublishResult>> PublishAsync(
        string tenantId,
        Guid clientId,
        ListingDraftPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty)
        {
            return Failure<ListingDraftPublishResult>("context", "INVALID_CONTEXT");
        }

        if (request.DraftId == Guid.Empty)
        {
            return Failure<ListingDraftPublishResult>("draftId", "DRAFT_NOT_FOUND");
        }

        if (string.IsNullOrWhiteSpace(request.RowVersion))
        {
            return Failure<ListingDraftPublishResult>("rowVersion", "ROWVERSION_REQUIRED");
        }

        var draft = await _dbContext.ListingDrafts.FirstOrDefaultAsync(
            item => item.DraftId == request.DraftId
                    && item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.MercadoLivre,
            cancellationToken);
        if (draft == null)
        {
            return Failure<ListingDraftPublishResult>("draftId", "DRAFT_NOT_FOUND");
        }

        if (draft.Status == ListingDraftStatus.Publishing)
        {
            return Failure<ListingDraftPublishResult>("status", "LISTING_PUBLISH_IN_PROGRESS");
        }

        var currentRowVersion = GetRowVersion(draft);
        if (!string.Equals(currentRowVersion, request.RowVersion, StringComparison.Ordinal))
        {
            return Failure<ListingDraftPublishResult>("rowVersion", "DRAFT_CONCURRENCY_CONFLICT");
        }

        if (draft.Status == ListingDraftStatus.Published && !string.IsNullOrWhiteSpace(draft.PublishedItemId))
        {
            var storedProviderDraft = ReadProviderDraftData(draft.ProviderDraftJson);
            var storedResults = storedProviderDraft.LastPublishResults
                .Select(item => new ListingDraftPublishVariationResult
                {
                    SabrVariantSku = item.SabrVariantSku,
                    Status = item.Status,
                    VariationId = item.VariationId,
                    ErrorCode = item.ErrorCode,
                    ErrorMessage = item.ErrorMessage,
                    PublishedAtUtc = item.PublishedAtUtc
                })
                .ToList();
            return ServiceResult<ListingDraftPublishResult>.Success(
                MapPublishResult(draft, currentRowVersion, null, new List<string>(), storedResults));
        }

        if (draft.Status != ListingDraftStatus.Valid)
        {
            return Failure<ListingDraftPublishResult>("status", "DRAFT_NOT_VALIDATED");
        }

        var validation = await ValidateDraftForPublishAsync(tenantId, clientId, draft, cancellationToken);
        if (!validation.Succeeded)
        {
            return ServiceResult<ListingDraftPublishResult>.Failure(validation.Errors);
        }

        var publishContext = validation.Data!;
        draft.Status = ListingDraftStatus.Publishing;
        draft.LastErrorAt = null;
        draft.LastErrorCode = null;
        draft.LastErrorMessage = null;
        draft.LastErrorRawJson = null;
        draft.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var warnings = new List<string>();
        var variationResults = new List<ListingDraftPublishVariationResult>();
        int? effectiveQuantity = null;

        try
        {
            var accessToken = await _oauthService.GetValidAccessTokenAsync(publishContext.Connection, cancellationToken);
            var normalizedPrice = ListingDraftHelpers.ToDecimal(draft.PriceCents!.Value);
            var changedSkus = new HashSet<string>(StringComparer.Ordinal);
            var nowUtc = DateTimeOffset.UtcNow;

            MercadoLivreCreateItemResult created;
            if (!publishContext.IsMultiVariation)
            {
                var variant = publishContext.Variants[0];
                var clamped = await ClampAvailableQuantityAsync(
                    tenantId,
                    clientId,
                    publishContext.Connection.SellerId,
                    draft.DraftId,
                    variant,
                    warnings,
                    cancellationToken);
                effectiveQuantity = clamped;

                created = await _mercadoLivreApiClient.CreateItemAsync(
                    new MercadoLivreCreateItemRequest
                    {
                        Title = publishContext.Title,
                        CategoryId = draft.CategoryId,
                        Price = normalizedPrice,
                        AvailableQuantity = clamped,
                        CurrencyId = "BRL",
                        ListingTypeId = draft.ListingTypeId!,
                        BuyingMode = "buy_it_now",
                        Condition = publishContext.ProviderDraft.Condition,
                        Description = publishContext.ProviderDraft.Description ?? publishContext.Product.Description,
                        PictureUrls = publishContext.PictureUrls.ToList(),
                        Attributes = publishContext.ItemAttributes.ToList(),
                        SellerCustomField = variant.VariantSku,
                        SabrVariantSku = variant.VariantSku
                    },
                    accessToken,
                    cancellationToken);
            }
            else
            {
                var variationPayload = new List<MercadoLivreCreateItemVariationRequest>();
                foreach (var variant in publishContext.Variants)
                {
                    var clamped = await ClampAvailableQuantityAsync(
                        tenantId,
                        clientId,
                        publishContext.Connection.SellerId,
                        draft.DraftId,
                        variant,
                        warnings,
                        cancellationToken);

                    var draftVariation = publishContext.ProviderDraft.Variations
                        .FirstOrDefault(item => string.Equals(item.SabrVariantSku, variant.VariantSku, StringComparison.Ordinal));
                    variationPayload.Add(new MercadoLivreCreateItemVariationRequest
                    {
                        SabrVariantSku = variant.VariantSku,
                        AvailableQuantity = clamped,
                        AttributeCombinations = draftVariation?.Attributes
                            .Select(attr => new MercadoLivreCreateItemVariationAttributeRequest
                            {
                                Id = attr.Id,
                                ValueId = attr.ValueId,
                                ValueName = attr.ValueName
                            })
                            .Where(attr => !string.IsNullOrWhiteSpace(attr.Id))
                            .ToList()
                            ?? new List<MercadoLivreCreateItemVariationAttributeRequest>(),
                        PictureUrls = draftVariation?.PictureIds?.ToList() ?? new List<string>()
                    });
                }

                created = await _mercadoLivreApiClient.CreateItemAsync(
                    new MercadoLivreCreateItemRequest
                    {
                        Title = publishContext.Title,
                        CategoryId = draft.CategoryId,
                        Price = normalizedPrice,
                        AvailableQuantity = 0,
                        CurrencyId = "BRL",
                        ListingTypeId = draft.ListingTypeId!,
                        BuyingMode = "buy_it_now",
                        Condition = publishContext.ProviderDraft.Condition,
                        Description = publishContext.ProviderDraft.Description ?? publishContext.Product.Description,
                        PictureUrls = publishContext.PictureUrls.ToList(),
                        Attributes = publishContext.ItemAttributes.ToList(),
                        SellerCustomField = draft.SabrVariantSku,
                        SabrVariantSku = draft.SabrVariantSku,
                        Variations = variationPayload
                    },
                    accessToken,
                    cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(created.ItemId))
            {
                throw new InvalidOperationException("ML publish response did not include item id.");
            }

            if (!publishContext.IsMultiVariation)
            {
                var singleVariant = publishContext.Variants[0];
                var mappingResult = await EnsureMappingAsync(
                    draft,
                    publishContext.Connection,
                    created.ItemId,
                    created.VariationId,
                    singleVariant.VariantSku,
                    cancellationToken);
                if (!mappingResult.Succeeded)
                {
                    draft.Status = ListingDraftStatus.Error;
                    draft.LastErrorAt = DateTimeOffset.UtcNow;
                    draft.LastErrorCode = "ML_MAPPING_CONFLICT";
                    draft.LastErrorMessage = "Mapping conflict for this listing.";
                    draft.LastErrorRawJson = ListingDraftHelpers.TrimTo32Kb(JsonSerializer.Serialize(new
                    {
                        created.ItemId,
                        created.VariationId,
                        singleVariant.VariantSku
                    }));
                    var failedProviderDraft = publishContext.ProviderDraft;
                    failedProviderDraft.LastPublishAttemptAtUtc = DateTimeOffset.UtcNow;
                    failedProviderDraft.LastPublishResults = new List<ProviderDraftPublishResult>
                    {
                        new()
                        {
                            SabrVariantSku = singleVariant.VariantSku,
                            Status = "Error",
                            ErrorCode = "ML_MAPPING_CONFLICT",
                            ErrorMessage = "Mapping conflict for this listing."
                        }
                    };
                    draft.ProviderDraftJson = BuildProviderDraftJson(draft, null, failedProviderDraft);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    return ServiceResult<ListingDraftPublishResult>.Failure(mappingResult.Errors);
                }

                changedSkus.Add(singleVariant.VariantSku);
                variationResults.Add(new ListingDraftPublishVariationResult
                {
                    SabrVariantSku = singleVariant.VariantSku,
                    Status = "Published",
                    VariationId = created.VariationId,
                    PublishedAtUtc = nowUtc
                });
            }
            else
            {
                var variationBySku = created.Variations
                    .Where(item => !string.IsNullOrWhiteSpace(item.SabrVariantSku))
                    .GroupBy(item => item.SabrVariantSku!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => Sku.Normalize(group.Key), group => group.First(), StringComparer.Ordinal);

                for (var index = 0; index < publishContext.Variants.Count; index++)
                {
                    var variant = publishContext.Variants[index];
                    variationBySku.TryGetValue(variant.VariantSku, out var variationResult);
                    var variationId = variationResult?.VariationId;
                    if (string.IsNullOrWhiteSpace(variationId)
                        && created.Variations.Count == publishContext.Variants.Count
                        && index < created.Variations.Count)
                    {
                        variationId = created.Variations[index].VariationId;
                    }
                    if (string.IsNullOrWhiteSpace(variationId))
                    {
                        variationResults.Add(new ListingDraftPublishVariationResult
                        {
                            SabrVariantSku = variant.VariantSku,
                            Status = "Error",
                            ErrorCode = "ML_VARIATION_ID_MISSING",
                            ErrorMessage = "Mercado Livre did not return variationId for this SKU."
                        });
                        continue;
                    }

                    var mappingResult = await EnsureMappingAsync(
                        draft,
                        publishContext.Connection,
                        created.ItemId,
                        variationId,
                        variant.VariantSku,
                        cancellationToken);
                    if (!mappingResult.Succeeded)
                    {
                        variationResults.Add(new ListingDraftPublishVariationResult
                        {
                            SabrVariantSku = variant.VariantSku,
                            Status = "Error",
                            ErrorCode = "ML_MAPPING_CONFLICT",
                            ErrorMessage = "Mapping conflict for this variation."
                        });
                        continue;
                    }

                    changedSkus.Add(variant.VariantSku);
                    variationResults.Add(new ListingDraftPublishVariationResult
                    {
                        SabrVariantSku = variant.VariantSku,
                        Status = "Published",
                        VariationId = variationId,
                        PublishedAtUtc = nowUtc
                    });
                }
            }

            var publishedCount = variationResults.Count(item => string.Equals(item.Status, "Published", StringComparison.OrdinalIgnoreCase));
            draft.PublishedItemId = created.ItemId;
            draft.PublishedVariationId = variationResults.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.VariationId))?.VariationId;
            draft.PublishedPermalink = ResolvePermalink(created.ItemId, created.Permalink);
            draft.PublishedApiUrl = ResolveApiUrl(created.ItemId, created.ApiUrl);
            draft.Status = publishedCount >= 1 ? ListingDraftStatus.Published : ListingDraftStatus.Error;
            draft.LastErrorAt = publishedCount >= 1 ? null : DateTimeOffset.UtcNow;
            draft.LastErrorCode = publishedCount >= 1 ? null : "ML_PUBLISH_FAILED";
            draft.LastErrorMessage = publishedCount >= 1 ? null : "No variation returned with publish success.";
            draft.LastErrorRawJson = publishedCount >= 1
                ? null
                : ListingDraftHelpers.TrimTo32Kb(JsonSerializer.Serialize(variationResults));
            draft.UpdatedAt = DateTimeOffset.UtcNow;

            publishContext.ProviderDraft.LastPublishAttemptAtUtc = DateTimeOffset.UtcNow;
            publishContext.ProviderDraft.LastPublishResults = variationResults
                .Select(item => new ProviderDraftPublishResult
                {
                    SabrVariantSku = item.SabrVariantSku,
                    Status = item.Status,
                    VariationId = item.VariationId,
                    ErrorCode = item.ErrorCode,
                    ErrorMessage = item.ErrorMessage,
                    PublishedAtUtc = item.PublishedAtUtc
                })
                .ToList();
            draft.ProviderDraftJson = BuildProviderDraftJson(draft, null, publishContext.ProviderDraft);
            await _dbContext.SaveChangesAsync(cancellationToken);

            if (changedSkus.Count > 0)
            {
                try
                {
                    await _stockAvailabilityService.SyncStockForSkusAsync(tenantId, clientId, changedSkus, cancellationToken);
                }
                catch (Exception stockEx)
                {
                    warnings.Add("ML_STOCK_SYNC_FAILED");
                    _logger.LogWarning(
                        stockEx,
                        "Stock sync after publish failed. tenant={TenantId} client={ClientId} draftId={DraftId}",
                        tenantId,
                        clientId,
                        draft.DraftId);
                }
            }

            var updatedRowVersion = GetRowVersion(draft);
            return ServiceResult<ListingDraftPublishResult>.Success(
                MapPublishResult(draft, updatedRowVersion, effectiveQuantity, warnings, variationResults));
        }
        catch (Exception ex) when (IsMlAuthInvalidException(ex) || IsUnauthorizedOrForbidden(ex))
        {
            MlPublishAuthInvalidCounter.Add(1);
            _logger.LogWarning(
                ex,
                "ml_publish_auth_invalid tenantId={TenantId} clientId={ClientId} integrationId={IntegrationId} sellerId={SellerId} categoryId={CategoryId} listingTypeId={ListingTypeId}",
                tenantId,
                clientId,
                draft.IntegrationId,
                draft.SellerId,
                draft.CategoryId,
                draft.ListingTypeId);

            await MarkPublishFailureAsync(draft, "ML_AUTH_INVALID", ex, cancellationToken);
            return Failure<ListingDraftPublishResult>("publish", "ML_AUTH_INVALID");
        }
        catch (Exception ex) when (IsMlPublishInputInvalidException(ex))
        {
            MlPublishInputInvalidCounter.Add(1);
            var mlEx = ex as MercadoLivreApiException;
            var rawBody = mlEx?.RawBody;
            var statusCode = mlEx?.StatusCode;
            _logger.LogWarning(
                ex,
                "ml_publish_input_invalid tenantId={TenantId} clientId={ClientId} integrationId={IntegrationId} sellerId={SellerId} categoryId={CategoryId} listingTypeId={ListingTypeId} draftId={DraftId} statusCode={StatusCode} rawBody={RawBody}",
                tenantId,
                clientId,
                draft.IntegrationId,
                draft.SellerId,
                draft.CategoryId,
                draft.ListingTypeId,
                draft.DraftId,
                statusCode,
                rawBody);

            await MarkPublishFailureAsync(draft, "ML_PUBLISH_INPUT_INVALID", ex, cancellationToken);
            return Failure<ListingDraftPublishResult>("publish", "ML_PUBLISH_INPUT_INVALID");
        }
        catch (Exception ex) when (IsMlTransientException(ex))
        {
            MlPublishUnavailableCounter.Add(1);
            _logger.LogWarning(
                ex,
                "ml_publish_unavailable tenantId={TenantId} clientId={ClientId} integrationId={IntegrationId} sellerId={SellerId} categoryId={CategoryId} listingTypeId={ListingTypeId}",
                tenantId,
                clientId,
                draft.IntegrationId,
                draft.SellerId,
                draft.CategoryId,
                draft.ListingTypeId);

            await MarkPublishFailureAsync(draft, "ML_UNAVAILABLE", ex, cancellationToken);
            return Failure<ListingDraftPublishResult>("publish", "ML_UNAVAILABLE");
        }
        catch (Exception ex)
        {
            await MarkPublishFailureAsync(draft, "ML_PUBLISH_FAILED", ex, cancellationToken);
            return Failure<ListingDraftPublishResult>("publish", "ML_PUBLISH_FAILED");
        }
    }

    public async Task<ServiceResult<ListingPublicationsQueryResult>> QueryPublicationsAsync(
        string tenantId,
        Guid clientId,
        ListingPublicationsQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty)
        {
            return Failure<ListingPublicationsQueryResult>("context", "INVALID_CONTEXT");
        }

        var skip = Math.Max(0, request.Skip);
        var limit = Math.Clamp(request.Limit <= 0 ? 20 : request.Limit, 1, 200);

        var query = _dbContext.ListingDrafts.AsNoTracking()
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == MarketplaceProvider.MercadoLivre);

        if (request.IntegrationId.HasValue && request.IntegrationId.Value != Guid.Empty)
        {
            query = query.Where(item => item.IntegrationId == request.IntegrationId.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.SellerId))
        {
            if (!MercadoLivreSellerIdParser.TryParseRequired(request.SellerId, out var parsedSeller))
            {
                return Failure<ListingPublicationsQueryResult>("sellerId", "SELLER_INVALID");
            }

            query = query.Where(item => item.SellerId == parsedSeller);
        }

        if (!string.IsNullOrWhiteSpace(request.Channel) && !IsMercadoLivreChannel(request.Channel))
        {
            return Failure<ListingPublicationsQueryResult>("channel", "CHANNEL_INVALID");
        }

        if (request.VariantSkus is { Count: > 0 })
        {
            var normalizedSkus = request.VariantSkus
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(Sku.Normalize)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (normalizedSkus.Count > 0)
            {
                query = query.Where(item => normalizedSkus.Contains(item.SabrVariantSku));
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            var normalizedStatus = request.Status.Trim();
            if (!QueryStatuses.Contains(normalizedStatus))
            {
                return Failure<ListingPublicationsQueryResult>("status", "STATUS_INVALID");
            }
            switch (normalizedStatus.ToUpperInvariant())
            {
                case "DRAFT":
                case "VALID":
                    query = query.Where(item => item.Status == ListingDraftStatus.Draft);
                    break;
                case "PUBLISHING":
                    query = query.Where(item => item.Status == ListingDraftStatus.Publishing);
                    break;
                case "PUBLISHED":
                    query = query.Where(item => item.Status == ListingDraftStatus.Published);
                    break;
                case "ERROR":
                    query = query.Where(item => item.Status == ListingDraftStatus.Error);
                    break;
                case "NONE":
                case "PAUSED":
                case "UNDERREVIEW":
                    query = query.Where(_ => false);
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim().ToUpperInvariant();
            query = query.Where(item =>
                item.BaseProductSku.ToUpper().Contains(term) ||
                item.SabrVariantSku.ToUpper().Contains(term) ||
                (item.PublishedItemId != null && item.PublishedItemId.ToUpper().Contains(term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var drafts = await query
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.DraftId)
            .Skip(skip)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var items = drafts.Select(item => new ListingPublicationItemResult
        {
            DraftId = item.DraftId,
            IntegrationId = item.IntegrationId,
            SellerId = MercadoLivreSellerIdParser.ToApiString(item.SellerId),
            BaseProductSku = item.BaseProductSku,
            SabrVariantSku = item.SabrVariantSku,
            Status = item.Status.ToString(),
            CategoryId = item.CategoryId,
            ListingTypeId = item.ListingTypeId,
            Price = item.PriceCents.HasValue ? ListingDraftHelpers.ToDecimal(item.PriceCents.Value) : null,
            CurrencyId = item.CurrencyId,
            PublishedItemId = item.PublishedItemId,
            PublishedVariationId = item.PublishedVariationId,
            PublishedPermalink = ResolvePermalink(item.PublishedItemId, item.PublishedPermalink),
            PublishedApiUrl = ResolveApiUrl(item.PublishedItemId, item.PublishedApiUrl),
            LastErrorCode = item.LastErrorCode,
            LastErrorMessage = item.LastErrorMessage,
            LastErrorAt = item.LastErrorAt,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        }).ToList();

        return ServiceResult<ListingPublicationsQueryResult>.Success(new ListingPublicationsQueryResult
        {
            Total = total,
            Skip = skip,
            Limit = limit,
            Items = items
        });
    }

    private async Task UpsertCategoryLockFromDraftAsync(
        string tenantId,
        Guid clientId,
        string baseProductSku,
        string? siteIdRaw,
        string? previousDraftCategoryIdRaw,
        string? draftCategoryIdRaw,
        string? requestCategoryIdRaw,
        bool categoryAutofilledNow,
        MarketplaceCategoryResolutionResult? resolution,
        string? traceId,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeAndValidateCategoryId(
                siteIdRaw,
                draftCategoryIdRaw,
                out var normalizedSiteId,
                out var normalizedCategoryId))
        {
            return;
        }

        var normalizedPreviousCategoryId = NormalizeCategoryId(previousDraftCategoryIdRaw);
        var normalizedRequestCategoryId = NormalizeCategoryId(requestCategoryIdRaw);
        var hasExplicitManualSelection = normalizedRequestCategoryId.Length > 0
                                         && !string.Equals(
                                             normalizedRequestCategoryId,
                                             normalizedPreviousCategoryId,
                                             StringComparison.OrdinalIgnoreCase);
        if (!hasExplicitManualSelection && !categoryAutofilledNow)
        {
            _logger.LogInformation(
                "listing_draft_category_lock_update_skipped_unchanged_category tenantId={TenantId} clientId={ClientId} baseSku={BaseSku} siteId={SiteId} previousCategoryId={PreviousCategoryId} currentCategoryId={CurrentCategoryId} traceId={TraceId}",
                tenantId,
                clientId,
                Sku.Normalize(baseProductSku),
                normalizedSiteId,
                normalizedPreviousCategoryId,
                normalizedCategoryId,
                traceId ?? string.Empty);
            return;
        }

        var normalizedBaseSku = Sku.Normalize(baseProductSku);
        var lockEntry = await _dbContext.ProductMarketplaceCategoryLocks
            .FirstOrDefaultAsync(
                item => item.TenantId == tenantId
                        && item.ClientId == clientId
                        && item.BaseProductSku == normalizedBaseSku
                        && item.SiteId == normalizedSiteId,
                cancellationToken);

        var matchesSuggestedCategory = !string.IsNullOrWhiteSpace(resolution?.SuggestedCategoryId)
            && string.Equals(
                NormalizeCategoryId(resolution.SuggestedCategoryId),
                normalizedCategoryId,
                StringComparison.OrdinalIgnoreCase);
        var persistAsManual = hasExplicitManualSelection && !matchesSuggestedCategory;
        var source = persistAsManual
            ? MarketplaceCategoryLockSource.Manual
            : MapCategoryLockSource(resolution?.SuggestedCategorySource);
        var status = persistAsManual
            ? MarketplaceCategoryLockStatus.ApprovedManual
            : MarketplaceCategoryLockStatus.ApprovedAuto;
        var label = await ResolveCategoryLockLabelAsync(
            normalizedCategoryId,
            resolution,
            accessToken,
            cancellationToken);
        var slugSnapshot = string.IsNullOrWhiteSpace(resolution?.InternalCategorySlug)
            ? null
            : resolution!.InternalCategorySlug!.Trim().ToLowerInvariant();

        if (lockEntry == null)
        {
            lockEntry = new ProductMarketplaceCategoryLock
            {
                TenantId = tenantId,
                ClientId = clientId,
                BaseProductSku = normalizedBaseSku,
                SiteId = normalizedSiteId
            };
            _dbContext.ProductMarketplaceCategoryLocks.Add(lockEntry);
        }

        lockEntry.ApprovedCategoryId = normalizedCategoryId;
        lockEntry.ApprovedCategoryName = label.CategoryName;
        lockEntry.ApprovedCategoryPath = label.CategoryPathFromRoot;
        lockEntry.Status = status;
        lockEntry.Source = source;
        lockEntry.InternalCategorySlugSnapshot = slugSnapshot;
    }

    private async Task MarkCategoryLockReviewRequiredAsync(
        string tenantId,
        Guid clientId,
        string baseProductSku,
        string siteIdRaw,
        CancellationToken cancellationToken)
    {
        var normalizedBaseSku = Sku.Normalize(baseProductSku);
        var normalizedSiteId = string.IsNullOrWhiteSpace(siteIdRaw) ? "MLB" : siteIdRaw.Trim().ToUpperInvariant();
        var lockEntry = await _dbContext.ProductMarketplaceCategoryLocks
            .FirstOrDefaultAsync(
                item => item.TenantId == tenantId
                        && item.ClientId == clientId
                        && item.BaseProductSku == normalizedBaseSku
                        && item.SiteId == normalizedSiteId,
                cancellationToken);
        if (lockEntry == null || lockEntry.Status == MarketplaceCategoryLockStatus.ReviewRequired)
        {
            return;
        }

        lockEntry.Status = MarketplaceCategoryLockStatus.ReviewRequired;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<CategoryLockLabel> ResolveCategoryLockLabelAsync(
        string normalizedCategoryId,
        MarketplaceCategoryResolutionResult? resolution,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        var suggestion = resolution?.Suggestions.FirstOrDefault(item =>
            string.Equals(item.CategoryId, normalizedCategoryId, StringComparison.OrdinalIgnoreCase));
        if (suggestion != null)
        {
            return new CategoryLockLabel(
                FirstNonEmpty(suggestion.CategoryName, normalizedCategoryId),
                FirstNonEmpty(suggestion.CategoryPathFromRoot, suggestion.CategoryName, normalizedCategoryId));
        }

        if (string.Equals(resolution?.SuggestedCategoryId, normalizedCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            return new CategoryLockLabel(
                FirstNonEmpty(resolution?.SuggestedCategoryPath, normalizedCategoryId),
                FirstNonEmpty(resolution?.SuggestedCategoryPath, normalizedCategoryId));
        }

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            try
            {
                var capabilities = await _mercadoLivreApiClient.GetCategoryCapabilitiesAsync(
                    normalizedCategoryId,
                    accessToken!,
                    cancellationToken);
                var categoryName = FirstNonEmpty(capabilities.CategoryName, normalizedCategoryId);
                var categoryPath = FirstNonEmpty(capabilities.CategoryPathFromRoot, categoryName, normalizedCategoryId);
                return new CategoryLockLabel(categoryName, categoryPath);
            }
            catch (Exception ex) when (IsMlTransientException(ex) || IsMlAuthInvalidException(ex) || IsUnauthorizedOrForbidden(ex))
            {
                _logger.LogInformation(
                    ex,
                    "category_lock_label_ml_unavailable categoryId={CategoryId}",
                    normalizedCategoryId);
            }
        }

        return new CategoryLockLabel(normalizedCategoryId, normalizedCategoryId);
    }

    private static MarketplaceCategoryLockSource MapCategoryLockSource(string? source)
    {
        var normalized = (source ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "lock" => MarketplaceCategoryLockSource.Lock,
            "domain_discovery" => MarketplaceCategoryLockSource.DomainDiscovery,
            "manual" => MarketplaceCategoryLockSource.Manual,
            _ => MarketplaceCategoryLockSource.Mapping
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private async Task<VariantResolutionContext?> ResolveVariantContextForGetAsync(
        string normalizedVariantSku,
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken)
    {
        var exactVariant = await _dbContext.ProductVariants
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.VariantSku == normalizedVariantSku, cancellationToken);
        if (exactVariant != null)
        {
            var exactVariants = await _dbContext.ProductVariants
                .AsNoTracking()
                .Where(item => item.BaseSku == exactVariant.BaseSku)
                .ToListAsync(cancellationToken);
            if (exactVariants.Count == 0)
            {
                exactVariants.Add(exactVariant);
            }

            return new VariantResolutionContext(exactVariant.BaseSku, exactVariants);
        }

        var baseVariants = await _dbContext.ProductVariants
            .AsNoTracking()
            .Where(item => item.BaseSku == normalizedVariantSku)
            .ToListAsync(cancellationToken);
        if (baseVariants.Count > 0)
        {
            return new VariantResolutionContext(normalizedVariantSku, baseVariants);
        }

        var product = await _dbContext.Products.AsNoTracking().FirstOrDefaultAsync(
            item => item.Sku == normalizedVariantSku && item.IsActive,
            cancellationToken);
        if (product == null)
        {
            return null;
        }

        var backfilled = await GetOrBackfillVariantAsync(
            normalizedVariantSku,
            tenantId,
            clientId,
            cancellationToken);
        if (backfilled == null)
        {
            return null;
        }

        var backfilledVariants = await _dbContext.ProductVariants
            .AsNoTracking()
            .Where(item => item.BaseSku == backfilled.BaseSku)
            .ToListAsync(cancellationToken);
        if (backfilledVariants.Count == 0)
        {
            backfilledVariants.Add(backfilled);
        }

        return new VariantResolutionContext(backfilled.BaseSku, backfilledVariants);
    }

    private async Task<ProductVariant?> GetOrBackfillVariantAsync(
        string normalizedVariantSku,
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken)
    {
        var variant = await _dbContext.ProductVariants.FirstOrDefaultAsync(
            item => item.VariantSku == normalizedVariantSku,
            cancellationToken);
        if (variant != null)
        {
            return variant;
        }

        var product = await _dbContext.Products.AsNoTracking().FirstOrDefaultAsync(
            item => item.Sku == normalizedVariantSku && item.IsActive,
            cancellationToken);
        if (product == null)
        {
            return null;
        }

        variant = new ProductVariant
        {
            VariantSku = normalizedVariantSku,
            BaseSku = product.Sku,
            Name = string.IsNullOrWhiteSpace(product.Name) ? product.Sku : product.Name.Trim(),
            CostPriceCents = Math.Max(0, product.CostPriceCents),
            CatalogPriceCents = Math.Max(0, product.CatalogPriceCents),
            PhysicalStock = 0,
            ReservedStock = 0,
            AvailableStock = 0,
            IsActive = product.IsActive,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.ProductVariants.Add(variant);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "PRODUCTVARIANT_BACKFILLED tenantId={TenantId} clientId={ClientId} variantSku={VariantSku}",
                tenantId,
                clientId,
                normalizedVariantSku);
            return variant;
        }
        catch (DbUpdateException ex)
        {
            _logger.LogInformation(
                ex,
                "ProductVariant backfill race handled. tenantId={TenantId} clientId={ClientId} variantSku={VariantSku}",
                tenantId,
                clientId,
                normalizedVariantSku);
            var existing = await _dbContext.ProductVariants.FirstOrDefaultAsync(
                item => item.VariantSku == normalizedVariantSku,
                cancellationToken);
            if (existing == null)
            {
                throw;
            }

            return existing;
        }
    }

    private static string BuildMaterialFingerprint(ListingDraft draft, ProviderDraftData providerDraft)
    {
        var normalizedImages = providerDraft.Images
            .Select(item => new
            {
                Url = (item.Url ?? string.Empty).Trim(),
                Position = item.Position
            })
            .OrderBy(item => item.Position)
            .ThenBy(item => item.Url, StringComparer.Ordinal)
            .ToList();

        var normalizedAttributes = providerDraft.Attributes
            .Select(item => new
            {
                Id = (item.Id ?? string.Empty).Trim().ToUpperInvariant(),
                ValueId = (item.ValueId ?? string.Empty).Trim(),
                ValueName = (item.ValueName ?? string.Empty).Trim()
            })
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ThenBy(item => item.ValueId, StringComparer.Ordinal)
            .ThenBy(item => item.ValueName, StringComparer.Ordinal)
            .ToList();

        var normalizedSelectedSkus = providerDraft.SelectedVariantSkus
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(Sku.Normalize)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();

        var normalizedAxes = providerDraft.VariationAxes
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();

        var normalizedVariations = providerDraft.Variations
            .Select(item => new
            {
                Sku = string.IsNullOrWhiteSpace(item.SabrVariantSku) ? string.Empty : Sku.Normalize(item.SabrVariantSku),
                Price = item.Price,
                InitialQuantity = item.InitialQuantity,
                Attributes = item.Attributes
                    .Select(attr => new
                    {
                        Id = (attr.Id ?? string.Empty).Trim().ToUpperInvariant(),
                        ValueId = (attr.ValueId ?? string.Empty).Trim(),
                        ValueName = (attr.ValueName ?? string.Empty).Trim()
                    })
                    .OrderBy(attr => attr.Id, StringComparer.Ordinal)
                    .ThenBy(attr => attr.ValueId, StringComparer.Ordinal)
                    .ThenBy(attr => attr.ValueName, StringComparer.Ordinal)
                    .ToList(),
                PictureIds = item.PictureIds
                    .Where(pic => !string.IsNullOrWhiteSpace(pic))
                    .Select(pic => pic.Trim())
                    .OrderBy(pic => pic, StringComparer.Ordinal)
                    .ToList()
            })
            .OrderBy(item => item.Sku, StringComparer.Ordinal)
            .ToList();

        var payload = new
        {
            integrationId = draft.IntegrationId,
            sellerId = draft.SellerId,
            channel = (providerDraft.Channel ?? string.Empty).Trim().ToLowerInvariant(),
            siteId = (providerDraft.SiteId ?? string.Empty).Trim().ToUpperInvariant(),
            categoryId = (draft.CategoryId ?? string.Empty).Trim(),
            listingTypeId = (draft.ListingTypeId ?? string.Empty).Trim(),
            condition = (providerDraft.Condition ?? string.Empty).Trim().ToLowerInvariant(),
            title = (providerDraft.Title ?? string.Empty).Trim(),
            description = (providerDraft.Description ?? string.Empty).Trim(),
            priceCents = draft.PriceCents,
            currencyId = (draft.CurrencyId ?? string.Empty).Trim().ToUpperInvariant(),
            gtin = (providerDraft.Gtin ?? string.Empty).Trim(),
            emptyGtinReason = (providerDraft.EmptyGtinReason ?? string.Empty).Trim(),
            ncm = (providerDraft.Ncm ?? string.Empty).Trim(),
            origin = (providerDraft.Origin ?? string.Empty).Trim(),
            images = normalizedImages,
            attributes = normalizedAttributes,
            publishMode = NormalizePublishMode(providerDraft.PublishMode),
            selectedVariantSkus = normalizedSelectedSkus,
            variationAxes = normalizedAxes,
            variations = normalizedVariations
        };

        return JsonSerializer.Serialize(payload, ProviderDraftJsonOptions);
    }

    private async Task<ServiceResult<PublishValidationContext>> ValidateDraftForPublishAsync(
        string tenantId,
        Guid clientId,
        ListingDraft draft,
        CancellationToken cancellationToken)
    {
        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.Id == draft.IntegrationId
                    && item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.MercadoLivre,
            cancellationToken);
        if (connection == null)
        {
            return Failure<PublishValidationContext>("integrationId", "INTEGRATION_REQUIRED");
        }

        if (draft.SellerId != connection.SellerId)
        {
            return Failure<PublishValidationContext>("sellerId", "SELLER_MISMATCH_FOR_INTEGRATION");
        }

        if (string.IsNullOrWhiteSpace(draft.CategoryId))
        {
            return Failure<PublishValidationContext>("categoryId", "CATEGORY_REQUIRED");
        }

        if (!ListingDraftHelpers.IsValidListingType(draft.ListingTypeId))
        {
            return Failure<PublishValidationContext>("listingTypeId", "LISTING_TYPE_INVALID");
        }

        if (!draft.PriceCents.HasValue || draft.PriceCents.Value <= 0)
        {
            return Failure<PublishValidationContext>("price", "PRICE_INVALID");
        }

        if (!string.Equals(ListingDraftHelpers.NormalizeCurrency(draft.CurrencyId), "BRL", StringComparison.Ordinal))
        {
            return Failure<PublishValidationContext>("currencyId", "CURRENCY_NOT_SUPPORTED");
        }

        var providerDraft = ReadProviderDraftData(draft.ProviderDraftJson);
        if (providerDraft.Variations.Any(item => item.Price.HasValue))
        {
            return Failure<PublishValidationContext>("variations", "PRICE_PER_VARIATION_NOT_SUPPORTED");
        }

        if (!string.IsNullOrWhiteSpace(providerDraft.Title) && providerDraft.Title.Length > MaxTitleLength)
        {
            return Failure<PublishValidationContext>("title", "TITLE_TOO_LONG");
        }

        var gtin = providerDraft.Gtin?.Trim();
        if (!string.IsNullOrWhiteSpace(gtin)
            && (gtin.Length < 8 || gtin.Length > 14 || !gtin.All(char.IsDigit)))
        {
            return Failure<PublishValidationContext>("gtin", "GTIN_INVALID");
        }

        var emptyGtinReason = providerDraft.EmptyGtinReason?.Trim();
        if (!string.IsNullOrWhiteSpace(gtin) && !string.IsNullOrWhiteSpace(emptyGtinReason))
        {
            return Failure<PublishValidationContext>("gtin", "GTIN_REASON_CONFLICT");
        }

        if (string.IsNullOrWhiteSpace(gtin) && string.IsNullOrWhiteSpace(emptyGtinReason))
        {
            return Failure<PublishValidationContext>("gtin", "GTIN_OR_REASON_REQUIRED");
        }

        if (!string.IsNullOrWhiteSpace(providerDraft.Ncm))
        {
            var ncm = providerDraft.Ncm.Trim();
            if (ncm.Length != 8 || !ncm.All(char.IsDigit))
            {
                return Failure<PublishValidationContext>("ncm", "NCM_INVALID");
            }
        }

        string accessToken;
        try
        {
            accessToken = await _oauthService.GetValidAccessTokenAsync(connection, cancellationToken);
        }
        catch (Exception ex) when (IsMlAuthInvalidException(ex))
        {
            return Failure<PublishValidationContext>("sellerId", "ML_AUTH_INVALID");
        }

        MercadoLivreCategoryCapabilityResponse capabilities;
        IReadOnlyList<MercadoLivreCategoryAttributeResponse> categoryAttributes;
        try
        {
            capabilities = await _mercadoLivreApiClient.GetCategoryCapabilitiesAsync(
                draft.CategoryId!.Trim(),
                accessToken,
                cancellationToken);
            categoryAttributes = await _mercadoLivreApiClient.GetCategoryAttributesAsync(
                draft.CategoryId.Trim(),
                accessToken,
                cancellationToken);
        }
        catch (Exception ex) when (IsUnauthorizedOrForbidden(ex))
        {
            try
            {
                accessToken = await _oauthService.GetValidAccessTokenAsync(connection, cancellationToken, forceRefresh: true);
            }
            catch (Exception refreshEx) when (IsMlAuthInvalidException(refreshEx))
            {
                return Failure<PublishValidationContext>("sellerId", "ML_AUTH_INVALID");
            }

            try
            {
                capabilities = await _mercadoLivreApiClient.GetCategoryCapabilitiesAsync(
                    draft.CategoryId!.Trim(),
                    accessToken,
                    cancellationToken);
                categoryAttributes = await _mercadoLivreApiClient.GetCategoryAttributesAsync(
                    draft.CategoryId.Trim(),
                    accessToken,
                    cancellationToken);
            }
            catch (Exception retryAuthEx) when (IsUnauthorizedOrForbidden(retryAuthEx))
            {
                return Failure<PublishValidationContext>("sellerId", "ML_AUTH_INVALID");
            }
            catch (Exception retryEx) when (IsMlCategoryInvalidException(retryEx))
            {
                _logger.LogWarning(
                    retryEx,
                    "listing_validate_category_invalid tenantId={TenantId} clientId={ClientId} integrationId={IntegrationId} sellerId={SellerId} categoryId={CategoryId} listingTypeId={ListingTypeId}",
                    tenantId,
                    clientId,
                    connection.Id,
                    connection.SellerId,
                    draft.CategoryId,
                    draft.ListingTypeId);
                return Failure<PublishValidationContext>("categoryId", "ML_CATEGORY_INVALID");
            }
            catch (Exception retryEx) when (IsMlTransientException(retryEx))
            {
                _logger.LogWarning(
                    retryEx,
                    "listing_validate_ml_unavailable tenantId={TenantId} clientId={ClientId} integrationId={IntegrationId} sellerId={SellerId} categoryId={CategoryId} listingTypeId={ListingTypeId}",
                    tenantId,
                    clientId,
                    connection.Id,
                    connection.SellerId,
                    draft.CategoryId,
                    draft.ListingTypeId);
                return Failure<PublishValidationContext>("categoryId", "ML_UNAVAILABLE");
            }
        }
        catch (Exception ex) when (IsMlCategoryInvalidException(ex))
        {
            _logger.LogWarning(
                ex,
                "listing_validate_category_invalid tenantId={TenantId} clientId={ClientId} integrationId={IntegrationId} sellerId={SellerId} categoryId={CategoryId} listingTypeId={ListingTypeId}",
                tenantId,
                clientId,
                connection.Id,
                connection.SellerId,
                draft.CategoryId,
                draft.ListingTypeId);
            return Failure<PublishValidationContext>("categoryId", "ML_CATEGORY_INVALID");
        }
        catch (Exception ex) when (IsMlTransientException(ex))
        {
            _logger.LogWarning(
                ex,
                "listing_validate_ml_unavailable tenantId={TenantId} clientId={ClientId} integrationId={IntegrationId} sellerId={SellerId} categoryId={CategoryId} listingTypeId={ListingTypeId}",
                tenantId,
                clientId,
                connection.Id,
                connection.SellerId,
                draft.CategoryId,
                draft.ListingTypeId);
            return Failure<PublishValidationContext>("categoryId", "ML_UNAVAILABLE");
        }

        var normalizedMode = NormalizePublishMode(providerDraft.PublishMode);
        var selectedSkus = normalizedMode == PublishModeMultiVariation
            ? providerDraft.SelectedVariantSkus
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(Sku.Normalize)
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : new List<string> { draft.SabrVariantSku };
        if (selectedSkus.Count == 0)
        {
            return Failure<PublishValidationContext>("selectedVariantSkus", "SELECTED_VARIANTS_REQUIRED");
        }

        if (normalizedMode == PublishModeMultiVariation)
        {
            if (!capabilities.AllowsVariations || capabilities.MaxVariationsAllowed <= 1)
            {
                return Failure<PublishValidationContext>("publishMode", "MULTI_VARIATION_NOT_ALLOWED");
            }

            if (capabilities.MaxVariationsAllowed > 0 && selectedSkus.Count > capabilities.MaxVariationsAllowed)
            {
                return Failure<PublishValidationContext>("selectedVariantSkus", "MAX_VARIATIONS_EXCEEDED");
            }

            var allowedVariationAttributes = capabilities.AllowedVariationAttributes.Count > 0
                ? capabilities.AllowedVariationAttributes
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : categoryAttributes
                    .Where(item => item.IsVariation)
                    .Select(item => item.Id)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            var allowedAxes = categoryAttributes
                .Where(item => item.IsVariation &&
                               (allowedVariationAttributes.Count == 0 ||
                                allowedVariationAttributes.Contains(item.Id, StringComparer.OrdinalIgnoreCase)))
                .Select(item => string.IsNullOrWhiteSpace(item.Name) ? item.Id : item.Name)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var normalizedAllowedAxes = allowedAxes
                .Select(NormalizeAxis)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToHashSet(StringComparer.Ordinal);

            for (var axisIndex = 0; axisIndex < providerDraft.VariationAxes.Count; axisIndex++)
            {
                var draftAxis = providerDraft.VariationAxes[axisIndex];
                var normalizedAxis = NormalizeAxis(draftAxis);
                if (string.IsNullOrWhiteSpace(normalizedAxis))
                {
                    continue;
                }

                if (!normalizedAllowedAxes.Contains(normalizedAxis))
                {
                    return Failure<PublishValidationContext>(
                        BuildAxisValidationFieldPath(axisIndex, draftAxis, allowedAxes),
                        "VARIATION_AXIS_NOT_ALLOWED");
                }
            }
        }

        var variants = await _dbContext.ProductVariants.AsNoTracking()
            .Where(item => selectedSkus.Contains(item.VariantSku))
            .ToListAsync(cancellationToken);
        if (variants.Count != selectedSkus.Count)
        {
            return Failure<PublishValidationContext>("sabrVariantSku", "SKU_NOT_FOUND");
        }

        var baseSku = variants[0].BaseSku;
        if (variants.Any(item => !string.Equals(item.BaseSku, baseSku, StringComparison.Ordinal)))
        {
            return Failure<PublishValidationContext>("selectedVariantSkus", "MIXED_BASE_SKU_NOT_SUPPORTED");
        }

        var product = await _dbContext.Products.AsNoTracking().FirstOrDefaultAsync(
            item => item.Sku == baseSku,
            cancellationToken);
        if (product == null)
        {
            return Failure<PublishValidationContext>("baseProductSku", "PRODUCT_NOT_FOUND");
        }

        var itemAttributes = BuildPublishItemAttributes(providerDraft, product, draft.SabrVariantSku);
        var requiredAttributeIds = categoryAttributes
            .Where(item => item.Required)
            .Select(item => item.Id)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (requiredAttributeIds.Count > 0)
        {
            var providedById = itemAttributes
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .ToDictionary(item => item.Id, item => item, StringComparer.OrdinalIgnoreCase);

            var missingRequiredAttribute = requiredAttributeIds
                .FirstOrDefault(requiredId =>
                    !providedById.TryGetValue(requiredId, out var provided) ||
                    !HasAttributeValue(provided));
            if (!string.IsNullOrWhiteSpace(missingRequiredAttribute))
            {
                return Failure<PublishValidationContext>($"attributes.{missingRequiredAttribute}", "ATTRIBUTE_REQUIRED");
            }
        }

        var title = !string.IsNullOrWhiteSpace(providerDraft.Title)
            ? providerDraft.Title.Trim()
            : normalizedMode == PublishModeMultiVariation
                ? BuildTitle(product.Name, null)
                : BuildTitle(product.Name, variants[0].Name);
        if (string.IsNullOrWhiteSpace(title))
        {
            return Failure<PublishValidationContext>("title", "TITLE_REQUIRED");
        }

        if (providerDraft.Images.Count > 0)
        {
            var sortedPositions = providerDraft.Images
                .OrderBy(item => item.Position)
                .Select(item => item.Position)
                .ToList();
            for (var expected = 1; expected <= sortedPositions.Count; expected++)
            {
                if (sortedPositions[expected - 1] != expected)
                {
                    return Failure<PublishValidationContext>("images", "IMAGE_POSITION_INVALID");
                }
            }
        }

        List<string> pictureUrls;
        if (providerDraft.Images.Count > 0)
        {
            pictureUrls = providerDraft.Images
                .OrderBy(item => item.Position)
                .Select(item => item.Url)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Take(10)
                .ToList();
        }
        else
        {
            pictureUrls = await _dbContext.ProductImages.AsNoTracking()
                .Where(item => item.ProductSku == baseSku)
                .OrderByDescending(item => item.IsPrimary)
                .ThenBy(item => item.SortOrder)
                .ThenBy(item => item.CreatedAt)
                .Select(item => item.Url)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Take(10)
                .ToListAsync(cancellationToken);
        }
        if (pictureUrls.Count == 0)
        {
            return Failure<PublishValidationContext>("pictures", "PICTURES_REQUIRED");
        }

        draft.ListingTypeId = ListingDraftHelpers.NormalizeListingTypeId(draft.ListingTypeId);
        draft.CurrencyId = "BRL";
        if (!string.Equals(draft.BaseProductSku, baseSku, StringComparison.Ordinal))
        {
            draft.BaseProductSku = baseSku;
        }
        providerDraft.PublishMode = normalizedMode;
        if (normalizedMode == PublishModeSingleVariant)
        {
            providerDraft.SelectedVariantSkus = new List<string> { draft.SabrVariantSku };
        }

        return ServiceResult<PublishValidationContext>.Success(new PublishValidationContext
        {
            Connection = connection,
            Product = product,
            Variants = variants.OrderBy(item => item.VariantSku, StringComparer.Ordinal).ToList(),
            Title = title,
            PictureUrls = pictureUrls,
            ItemAttributes = itemAttributes,
            ProviderDraft = providerDraft,
            IsMultiVariation = normalizedMode == PublishModeMultiVariation
        });
    }

    private static string BuildTitle(string? productName, string? variantName)
    {
        var productPart = string.IsNullOrWhiteSpace(productName) ? string.Empty : productName.Trim();
        var variantPart = string.IsNullOrWhiteSpace(variantName) ? string.Empty : variantName.Trim();

        if (string.IsNullOrWhiteSpace(productPart) && string.IsNullOrWhiteSpace(variantPart))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(variantPart))
        {
            return productPart;
        }

        if (string.Equals(productPart, variantPart, StringComparison.OrdinalIgnoreCase))
        {
            return productPart;
        }

        return string.IsNullOrWhiteSpace(productPart)
            ? variantPart
            : $"{productPart} - {variantPart}";
    }

    private static List<MercadoLivreCreateItemAttributeRequest> BuildPublishItemAttributes(
        ProviderDraftData providerDraft,
        Product product,
        string sellerSku)
    {
        var attributesById = new Dictionary<string, MercadoLivreCreateItemAttributeRequest>(StringComparer.OrdinalIgnoreCase);

        foreach (var attribute in providerDraft.Attributes)
        {
            AddOrUpdateAttribute(
                attributesById,
                attribute.Id,
                attribute.ValueId,
                attribute.ValueName,
                overwrite: true);
        }

        var gtin = providerDraft.Gtin?.Trim();
        var emptyGtinReason = providerDraft.EmptyGtinReason?.Trim();
        if (!string.IsNullOrWhiteSpace(gtin))
        {
            attributesById.Remove("EMPTY_GTIN_REASON");
            AddOrUpdateAttribute(attributesById, "GTIN", null, gtin, overwrite: false);
        }
        else if (!string.IsNullOrWhiteSpace(emptyGtinReason))
        {
            attributesById.Remove("GTIN");
            AddOrUpdateAttribute(attributesById, "EMPTY_GTIN_REASON", null, emptyGtinReason, overwrite: false);
        }

        AddOrUpdateAttribute(attributesById, "NCM", null, providerDraft.Ncm, overwrite: false);
        AddOrUpdateAttribute(attributesById, "ORIGIN", null, providerDraft.Origin, overwrite: false);
        AddOrUpdateAttribute(attributesById, "BRAND", null, product.Brand, overwrite: false);
        AddOrUpdateAttribute(attributesById, "SELLER_SKU", null, sellerSku, overwrite: true);

        return attributesById.Values
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddOrUpdateAttribute(
        IDictionary<string, MercadoLivreCreateItemAttributeRequest> attributesById,
        string? id,
        string? valueId,
        string? valueName,
        bool overwrite)
    {
        var normalizedId = id?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            return;
        }

        var normalizedValueId = string.IsNullOrWhiteSpace(valueId) ? null : valueId.Trim();
        var normalizedValueName = string.IsNullOrWhiteSpace(valueName) ? null : valueName.Trim();
        if (string.IsNullOrWhiteSpace(normalizedValueId) && string.IsNullOrWhiteSpace(normalizedValueName))
        {
            return;
        }

        if (attributesById.TryGetValue(normalizedId, out var existing))
        {
            if (overwrite)
            {
                existing.ValueId = normalizedValueId;
                existing.ValueName = normalizedValueName;
            }

            return;
        }

        attributesById[normalizedId] = new MercadoLivreCreateItemAttributeRequest
        {
            Id = normalizedId,
            ValueId = normalizedValueId,
            ValueName = normalizedValueName
        };
    }

    private static bool HasAttributeValue(MercadoLivreCreateItemAttributeRequest attribute)
    {
        return !string.IsNullOrWhiteSpace(attribute.ValueId) || !string.IsNullOrWhiteSpace(attribute.ValueName);
    }

    private async Task MarkPublishFailureAsync(
        ListingDraft draft,
        string errorCode,
        Exception ex,
        CancellationToken cancellationToken)
    {
        var providerDraft = ReadProviderDraftData(draft.ProviderDraftJson);
        providerDraft.LastPublishAttemptAtUtc = DateTimeOffset.UtcNow;
        providerDraft.LastPublishResults = new List<ProviderDraftPublishResult>();
        draft.ProviderDraftJson = BuildProviderDraftJson(draft, null, providerDraft);
        draft.Status = ListingDraftStatus.Error;
        draft.LastErrorCode = errorCode;
        draft.LastErrorMessage = ex.Message;
        draft.LastErrorAt = DateTimeOffset.UtcNow;
        var mlApiEx = ex as MercadoLivreApiException;
        draft.LastErrorRawJson = ListingDraftHelpers.TrimTo32Kb(mlApiEx?.RawBody ?? ex.ToString());
        draft.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<ServiceResult<bool>> EnsureMappingAsync(
        ListingDraft draft,
        TenantMarketplaceConnection connection,
        string mlItemId,
        string? mlVariationId,
        string sabrVariantSku,
        CancellationToken cancellationToken)
    {
        var existing = await _dbContext.TenantMarketplaceListingMaps.FirstOrDefaultAsync(
            item => item.TenantId == draft.TenantId
                    && item.ClientId == draft.ClientId
                    && item.Provider == MarketplaceProvider.MercadoLivre
                    && item.IntegrationId == connection.Id
                    && item.SellerId == connection.SellerId
                    && item.MlItemId == mlItemId
                    && item.MlVariationId == mlVariationId,
            cancellationToken);
        if (existing != null)
        {
            if (!string.Equals(existing.SabrVariantSku, sabrVariantSku, StringComparison.Ordinal))
            {
                return Failure<bool>("mapping", "ML_MAPPING_CONFLICT");
            }

            return ServiceResult<bool>.Success(true);
        }

        _dbContext.TenantMarketplaceListingMaps.Add(new TenantMarketplaceListingMap
        {
            Id = Guid.NewGuid(),
            TenantId = draft.TenantId,
            ClientId = draft.ClientId,
            Provider = MarketplaceProvider.MercadoLivre,
            IntegrationId = connection.Id,
            SellerId = connection.SellerId,
            MlItemId = mlItemId,
            MlVariationId = mlVariationId,
            SabrVariantSku = sabrVariantSku,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        return ServiceResult<bool>.Success(true);
    }

    private void ApplyPatch(
        ListingDraft draft,
        ListingDraftUpsertRequest request,
        HashSet<string> clearSet,
        long sellerIdFromIntegration)
    {
        if (clearSet.Contains("categoryId"))
        {
            draft.CategoryId = null;
        }
        else if (request.CategoryId != null)
        {
            draft.CategoryId = string.IsNullOrWhiteSpace(request.CategoryId) ? null : request.CategoryId.Trim();
        }

        if (clearSet.Contains("listingTypeId"))
        {
            draft.ListingTypeId = null;
        }
        else if (request.ListingTypeId != null)
        {
            draft.ListingTypeId = string.IsNullOrWhiteSpace(request.ListingTypeId)
                ? null
                : ListingDraftHelpers.NormalizeListingTypeId(request.ListingTypeId);
        }

        if (clearSet.Contains("price"))
        {
            draft.PriceCents = null;
        }
        else if (request.Price.HasValue)
        {
            draft.PriceCents = ListingDraftHelpers.ToCents(request.Price.Value);
        }

        if (clearSet.Contains("currencyId"))
        {
            draft.CurrencyId = "BRL";
        }
        else if (request.CurrencyId != null)
        {
            draft.CurrencyId = ListingDraftHelpers.NormalizeCurrency(request.CurrencyId);
        }

        draft.SellerId = sellerIdFromIntegration;
        draft.Provider = MarketplaceProvider.MercadoLivre;
        draft.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string BuildProviderDraftJson(
        ListingDraft draft,
        ListingDraftUpsertRequest? request,
        ProviderDraftData providerData)
    {
        if (request?.ProductCost.HasValue == true)
        {
            providerData.ProductCostCents = ListingDraftHelpers.ToCents(request.ProductCost.Value);
        }

        if (request?.OperationalCost.HasValue == true)
        {
            providerData.OperationalCostCents = ListingDraftHelpers.ToCents(request.OperationalCost.Value);
        }

        var payload = new
        {
            channel = providerData.Channel,
            siteId = providerData.SiteId,
            integrationId = draft.IntegrationId,
            sellerId = draft.SellerId,
            baseProductSku = draft.BaseProductSku,
            sabrVariantSku = draft.SabrVariantSku,
            categoryId = draft.CategoryId,
            listingTypeId = draft.ListingTypeId,
            condition = providerData.Condition,
            title = providerData.Title,
            description = providerData.Description,
            priceCents = draft.PriceCents,
            currencyId = draft.CurrencyId,
            gtin = providerData.Gtin,
            emptyGtinReason = providerData.EmptyGtinReason,
            ncm = providerData.Ncm,
            origin = providerData.Origin,
            images = providerData.Images,
            attributes = providerData.Attributes,
            productCostCents = providerData.ProductCostCents,
            operationalCostCents = providerData.OperationalCostCents,
            publishMode = providerData.PublishMode,
            selectedVariantSkus = providerData.SelectedVariantSkus,
            variationAxes = providerData.VariationAxes,
            variations = providerData.Variations,
            lastPublishAttemptAtUtc = providerData.LastPublishAttemptAtUtc,
            lastPublishResults = providerData.LastPublishResults,
            updatedAt = draft.UpdatedAt
        };

        return JsonSerializer.Serialize(payload, ProviderDraftJsonOptions);
    }

    private ListingDraftResult MapListingDraftResult(ListingDraft draft, string rowVersion)
    {
        var providerDraft = ReadProviderDraftData(draft.ProviderDraftJson);
        return new ListingDraftResult
        {
            DraftId = draft.DraftId,
            IntegrationId = draft.IntegrationId,
            Channel = providerDraft.Channel,
            SellerId = MercadoLivreSellerIdParser.ToApiString(draft.SellerId),
            SiteId = providerDraft.SiteId,
            BaseProductSku = draft.BaseProductSku,
            SabrVariantSku = draft.SabrVariantSku,
            CategoryId = draft.CategoryId,
            ListingTypeId = draft.ListingTypeId,
            Condition = providerDraft.Condition,
            Title = providerDraft.Title,
            Description = providerDraft.Description,
            Price = draft.PriceCents.HasValue ? ListingDraftHelpers.ToDecimal(draft.PriceCents.Value) : null,
            CurrencyId = draft.CurrencyId,
            Gtin = providerDraft.Gtin,
            EmptyGtinReason = providerDraft.EmptyGtinReason,
            Ncm = providerDraft.Ncm,
            Origin = providerDraft.Origin,
            Images = providerDraft.Images.Select(item => new ListingDraftImageRequest
            {
                Url = item.Url,
                Position = item.Position
            }).ToList(),
            Attributes = providerDraft.Attributes.Select(item => new ListingDraftAttributeRequest
            {
                Id = item.Id,
                ValueId = item.ValueId,
                ValueName = item.ValueName
            }).ToList(),
            Status = draft.Status.ToString(),
            RowVersion = rowVersion,
            UpdatedAt = draft.UpdatedAt,
            Warnings = new List<string>(),
            LastErrorCode = draft.LastErrorCode,
            LastErrorMessage = draft.LastErrorMessage,
            LastErrorAt = draft.LastErrorAt,
            PublishedItemId = draft.PublishedItemId,
            PublishedVariationId = draft.PublishedVariationId,
            PublishedPermalink = ResolvePermalink(draft.PublishedItemId, draft.PublishedPermalink),
            PublishedApiUrl = ResolveApiUrl(draft.PublishedItemId, draft.PublishedApiUrl)
        };
    }

    private ListingDraftPublishResult MapPublishResult(
        ListingDraft draft,
        string rowVersion,
        int? effectiveQuantity,
        List<string> warnings,
        List<ListingDraftPublishVariationResult> variationResults)
    {
        return new ListingDraftPublishResult
        {
            DraftId = draft.DraftId,
            Status = draft.Status.ToString(),
            RowVersion = rowVersion,
            UpdatedAt = draft.UpdatedAt,
            PublishedItemId = draft.PublishedItemId,
            PublishedVariationId = draft.PublishedVariationId,
            PublishedPermalink = ResolvePermalink(draft.PublishedItemId, draft.PublishedPermalink),
            PublishedApiUrl = ResolveApiUrl(draft.PublishedItemId, draft.PublishedApiUrl),
            EffectiveQuantity = effectiveQuantity,
            Warnings = warnings,
            VariationResults = variationResults,
            LastErrorCode = draft.LastErrorCode,
            LastErrorMessage = draft.LastErrorMessage,
            LastErrorAt = draft.LastErrorAt
        };
    }

    private static string NormalizePublishMode(string? publishMode)
    {
        if (string.Equals(publishMode, PublishModeMultiVariation, StringComparison.OrdinalIgnoreCase))
        {
            return PublishModeMultiVariation;
        }

        return PublishModeSingleVariant;
    }

    private static ProviderDraftData ReadProviderDraftData(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new ProviderDraftData();
        }

        try
        {
            var data = JsonSerializer.Deserialize<ProviderDraftData>(rawJson, ProviderDraftJsonOptions);
            if (data == null)
            {
                return new ProviderDraftData();
            }

            data.PublishMode = NormalizePublishMode(data.PublishMode);
            data.Channel = string.IsNullOrWhiteSpace(data.Channel) ? MercadoLivreChannel : data.Channel.Trim().ToLowerInvariant();
            data.SiteId = string.IsNullOrWhiteSpace(data.SiteId) ? "MLB" : data.SiteId.Trim().ToUpperInvariant();
            data.Condition = string.IsNullOrWhiteSpace(data.Condition) ? "new" : data.Condition.Trim().ToLowerInvariant();
            data.Title = data.Title?.Trim();
            data.Description = data.Description?.Trim();
            data.Gtin = data.Gtin?.Trim();
            data.EmptyGtinReason = data.EmptyGtinReason?.Trim();
            data.Ncm = data.Ncm?.Trim();
            data.Origin = data.Origin?.Trim();
            data.Images = data.Images
                .Where(item => !string.IsNullOrWhiteSpace(item.Url))
                .Select(item => new ProviderDraftImage
                {
                    Url = item.Url.Trim(),
                    Position = item.Position <= 0 ? 1 : item.Position
                })
                .OrderBy(item => item.Position)
                .ToList();
            data.Attributes = data.Attributes
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .Select(item => new ProviderDraftAttribute
                {
                    Id = item.Id.Trim(),
                    ValueId = item.ValueId?.Trim(),
                    ValueName = item.ValueName?.Trim()
                })
                .ToList();
            data.SelectedVariantSkus = data.SelectedVariantSkus
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(Sku.Normalize)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            data.VariationAxes = data.VariationAxes
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            data.Variations = data.Variations
                .Where(item => !string.IsNullOrWhiteSpace(item.SabrVariantSku))
                .Select(item =>
                {
                    item.SabrVariantSku = Sku.Normalize(item.SabrVariantSku);
                    item.Attributes = item.Attributes
                        .Where(attr => !string.IsNullOrWhiteSpace(attr.Id))
                        .ToList();
                    item.PictureIds = item.PictureIds
                        .Where(pic => !string.IsNullOrWhiteSpace(pic))
                        .Select(pic => pic.Trim())
                        .ToList();
                    return item;
                })
                .ToList();
            data.LastPublishResults = data.LastPublishResults ?? new List<ProviderDraftPublishResult>();
            return data;
        }
        catch (JsonException)
        {
            return new ProviderDraftData();
        }
    }

    private static void ApplyProviderPatch(
        ProviderDraftData providerData,
        ListingDraftUpsertRequest request,
        HashSet<string> clearSet)
    {
        if (clearSet.Contains("channel"))
        {
            providerData.Channel = MercadoLivreChannel;
        }
        else if (!string.IsNullOrWhiteSpace(request.Channel))
        {
            providerData.Channel = request.Channel.Trim().ToLowerInvariant();
        }

        if (clearSet.Contains("siteId"))
        {
            providerData.SiteId = "MLB";
        }
        else if (request.SiteId != null)
        {
            providerData.SiteId = string.IsNullOrWhiteSpace(request.SiteId) ? "MLB" : request.SiteId.Trim().ToUpperInvariant();
        }

        if (clearSet.Contains("condition"))
        {
            providerData.Condition = "new";
        }
        else if (request.Condition != null)
        {
            providerData.Condition = string.IsNullOrWhiteSpace(request.Condition) ? "new" : request.Condition.Trim().ToLowerInvariant();
        }

        if (clearSet.Contains("title"))
        {
            providerData.Title = null;
        }
        else if (request.Title != null)
        {
            providerData.Title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim();
        }

        if (clearSet.Contains("description"))
        {
            providerData.Description = null;
        }
        else if (request.Description != null)
        {
            providerData.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        }

        if (clearSet.Contains("gtin"))
        {
            providerData.Gtin = null;
        }
        else if (request.Gtin != null)
        {
            providerData.Gtin = string.IsNullOrWhiteSpace(request.Gtin) ? null : request.Gtin.Trim();
        }

        if (clearSet.Contains("emptyGtinReason"))
        {
            providerData.EmptyGtinReason = null;
        }
        else if (request.EmptyGtinReason != null)
        {
            providerData.EmptyGtinReason = string.IsNullOrWhiteSpace(request.EmptyGtinReason) ? null : request.EmptyGtinReason.Trim();
        }

        if (clearSet.Contains("ncm"))
        {
            providerData.Ncm = null;
        }
        else if (request.Ncm != null)
        {
            providerData.Ncm = string.IsNullOrWhiteSpace(request.Ncm) ? null : request.Ncm.Trim();
        }

        if (clearSet.Contains("origin"))
        {
            providerData.Origin = null;
        }
        else if (request.Origin != null)
        {
            providerData.Origin = string.IsNullOrWhiteSpace(request.Origin) ? null : request.Origin.Trim();
        }

        if (clearSet.Contains("images"))
        {
            providerData.Images.Clear();
        }
        else if (request.Images != null)
        {
            providerData.Images = request.Images
                .Where(item => !string.IsNullOrWhiteSpace(item.Url))
                .Select(item => new ProviderDraftImage
                {
                    Url = item.Url.Trim(),
                    Position = item.Position <= 0 ? 1 : item.Position
                })
                .OrderBy(item => item.Position)
                .ToList();
        }

        if (clearSet.Contains("attributes"))
        {
            providerData.Attributes.Clear();
        }
        else if (request.Attributes != null)
        {
            providerData.Attributes = request.Attributes
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .Select(item => new ProviderDraftAttribute
                {
                    Id = item.Id.Trim(),
                    ValueId = item.ValueId?.Trim(),
                    ValueName = item.ValueName?.Trim()
                })
                .ToList();
        }

        if (clearSet.Contains("publishMode"))
        {
            providerData.PublishMode = PublishModeSingleVariant;
        }
        else if (request.PublishMode != null)
        {
            providerData.PublishMode = NormalizePublishMode(request.PublishMode);
        }

        if (clearSet.Contains("selectedVariantSkus"))
        {
            providerData.SelectedVariantSkus.Clear();
        }
        else if (request.SelectedVariantSkus != null)
        {
            providerData.SelectedVariantSkus = request.SelectedVariantSkus
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(Sku.Normalize)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        if (clearSet.Contains("variationAxes"))
        {
            providerData.VariationAxes.Clear();
        }
        else if (request.VariationAxes != null)
        {
            providerData.VariationAxes = request.VariationAxes
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (clearSet.Contains("variations"))
        {
            providerData.Variations.Clear();
        }
        else if (request.Variations != null)
        {
            providerData.Variations = request.Variations
                .Where(item => !string.IsNullOrWhiteSpace(item.SabrVariantSku))
                .Select(item => new ProviderDraftVariation
                {
                    SabrVariantSku = Sku.Normalize(item.SabrVariantSku),
                    Price = item.Price,
                    InitialQuantity = item.InitialQuantity,
                    Attributes = item.Attributes
                        .Where(attr => !string.IsNullOrWhiteSpace(attr.Id))
                        .Select(attr => new ProviderDraftVariationAttribute
                        {
                            Id = attr.Id.Trim(),
                            ValueId = attr.ValueId?.Trim(),
                            ValueName = attr.ValueName?.Trim()
                        })
                        .ToList(),
                    PictureIds = item.PictureIds
                        .Where(pic => !string.IsNullOrWhiteSpace(pic))
                        .Select(pic => pic.Trim())
                        .ToList()
                })
                .ToList();
        }

        if (clearSet.Contains("productCost"))
        {
            providerData.ProductCostCents = null;
        }

        if (clearSet.Contains("operationalCost"))
        {
            providerData.OperationalCostCents = null;
        }
    }

    private async Task<int> ClampAvailableQuantityAsync(
        string tenantId,
        Guid clientId,
        long sellerId,
        Guid draftId,
        ProductVariant variant,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var rawAvailable = variant.PhysicalStock - variant.ReservedStock;
        var clampedAvailable = Math.Clamp(rawAvailable, 0, MaxPublishQuantity);
        if (rawAvailable < 0 || rawAvailable > MaxPublishQuantity)
        {
            warnings.Add("ML_STOCK_INVALID_FORCED_ZERO");
            clampedAvailable = rawAvailable < 0 ? 0 : MaxPublishQuantity;
            await _marketplaceAuditLogService.RecordAsync(
                tenantId,
                clientId,
                MarketplaceProvider.MercadoLivre,
                sellerId,
                "ml.stock.invalid.forced_zero",
                draftId.ToString("N"),
                new
                {
                    draftId,
                    sku = variant.VariantSku,
                    rawAvailable,
                    effectiveQuantity = clampedAvailable
                },
                "publish",
                cancellationToken);
        }

        return clampedAvailable;
    }

    private static ListingDraftValidationIssueResult MapValidationIssue(ValidationError error)
    {
        var fieldPath = error.Field;
        var step = ResolveValidationStep(error.Field, error.Message);
        var message = error.Message switch
        {
            "INTEGRATION_REQUIRED" => "Integration is required.",
            "INVALID_SELLER_INTEGRATION" => "Seller does not belong to selected integration.",
            "CHANNEL_INVALID" => "Channel must be Mercado Livre in this phase.",
            "CATEGORY_REQUIRED" => "Category is required.",
            "LISTING_TYPE_INVALID" => "Listing type must be gold_special or gold_pro.",
            "PRICE_INVALID" => "Price must be greater than zero.",
            "CURRENCY_NOT_SUPPORTED" => "Only BRL is supported.",
            "TITLE_REQUIRED" => "Title is required.",
            "TITLE_TOO_LONG" => "Title must have at most 60 characters.",
            "PICTURES_REQUIRED" => "At least one image is required.",
            "IMAGE_POSITION_INVALID" => "Image positions must be sequential (1..N).",
            "GTIN_OR_REASON_REQUIRED" => "Provide GTIN or EMPTY_GTIN_REASON.",
            "GTIN_INVALID" => "GTIN must have 8 to 14 numeric digits.",
            "GTIN_REASON_CONFLICT" => "When GTIN is provided, EMPTY_GTIN_REASON must be empty.",
            "NCM_INVALID" => "NCM must have 8 numeric digits.",
            "SELLER_MISMATCH_FOR_INTEGRATION" => "Seller does not match integration.",
            "PRICE_PER_VARIATION_NOT_SUPPORTED" => "Price per variation is not supported.",
            "ATTRIBUTE_REQUIRED" => "Required category attribute is missing.",
            "ML_AUTH_INVALID" => "Mercado Livre authentication is invalid. Reconnect integration.",
            "ML_CATEGORY_INVALID" => "CategoryId is invalid in Mercado Livre.",
            "ML_UNAVAILABLE" => "Mercado Livre is unavailable at the moment.",
            "MULTI_VARIATION_NOT_ALLOWED" => "Selected category does not support multiple variations.",
            "MAX_VARIATIONS_EXCEEDED" => "Selected variants exceed category maximum.",
            "VARIATION_AXIS_NOT_ALLOWED" => BuildAxisValidationMessage(error.Field, out fieldPath),
            _ => error.Message
        };

        return new ListingDraftValidationIssueResult
        {
            FieldPath = fieldPath,
            Code = error.Message,
            Message = message,
            Severity = "error",
            Step = step
        };
    }

    private static string ResolveValidationStep(string fieldPath, string code)
    {
        var field = (fieldPath ?? string.Empty).Trim().ToLowerInvariant();
        if (field.Contains("seller") || field.Contains("integration"))
        {
            return "seller";
        }

        if (field.Contains("category"))
        {
            return "category";
        }

        if (field.Contains("title") || field.Contains("description"))
        {
            return "content";
        }

        if (field.Contains("variation") || field.Contains("attribute"))
        {
            return "attributes";
        }

        if (field.Contains("price") || field.Contains("currency") || field.Contains("listingtype"))
        {
            return "pricing";
        }

        if (field.Contains("gtin") || field.Contains("ncm") || field.Contains("origin"))
        {
            return "fiscal";
        }

        if (field.Contains("picture") || field.Contains("image"))
        {
            return "images";
        }

        return code switch
        {
            "TITLE_REQUIRED" or "TITLE_TOO_LONG" => "content",
            "CATEGORY_REQUIRED" => "category",
            "PRICE_INVALID" or "CURRENCY_NOT_SUPPORTED" or "LISTING_TYPE_INVALID" => "pricing",
            "GTIN_OR_REASON_REQUIRED" or "NCM_INVALID" => "fiscal",
            "GTIN_INVALID" or "GTIN_REASON_CONFLICT" => "fiscal",
            "PICTURES_REQUIRED" or "IMAGE_POSITION_INVALID" => "images",
            "ATTRIBUTE_REQUIRED" => "attributes",
            "ML_CATEGORY_INVALID" => "category",
            "ML_AUTH_INVALID" => "seller",
            "ML_UNAVAILABLE" => "review",
            "VARIATION_AXIS_NOT_ALLOWED" or "MULTI_VARIATION_NOT_ALLOWED" or "MAX_VARIATIONS_EXCEEDED" => "attributes",
            _ => "review"
        };
    }

    private static string BuildAxisValidationFieldPath(int index, string axis, IReadOnlyCollection<string> allowedAxes)
    {
        var allowed = allowedAxes.Count == 0 ? string.Empty : string.Join(", ", allowedAxes);
        return $"variationAxes[{index}]{AxisValidationMetadataSeparator}{axis.Trim()}{AxisValidationMetadataSeparator}{allowed}";
    }

    private static string NormalizeAxis(string? rawAxis)
    {
        return string.IsNullOrWhiteSpace(rawAxis)
            ? string.Empty
            : rawAxis.Trim().ToUpperInvariant();
    }

    private static string BuildAxisValidationMessage(string rawField, out string fieldPath)
    {
        fieldPath = rawField;
        if (string.IsNullOrWhiteSpace(rawField) || !rawField.Contains(AxisValidationMetadataSeparator, StringComparison.Ordinal))
        {
            return "Variation axis is not allowed for selected category.";
        }

        var parts = rawField.Split(AxisValidationMetadataSeparator, 3, StringSplitOptions.None);
        fieldPath = parts[0];
        var rejectedAxis = parts.Length > 1 ? parts[1] : string.Empty;
        var allowed = parts.Length > 2 ? parts[2] : string.Empty;
        if (string.IsNullOrWhiteSpace(rejectedAxis))
        {
            return "Variation axis is not allowed for selected category.";
        }

        return string.IsNullOrWhiteSpace(allowed)
            ? $"Variation axis '{rejectedAxis}' is not allowed for selected category."
            : $"Variation axis '{rejectedAxis}' is not allowed. Allowed axes: {allowed}.";
    }

    private static bool TryValidateSellerCompatibility(string? rawSellerId, long expectedSellerId, out string? errorCode)
    {
        errorCode = null;
        if (string.IsNullOrWhiteSpace(rawSellerId))
        {
            return true;
        }

        if (!MercadoLivreSellerIdParser.TryParseRequired(rawSellerId, out var parsedSellerId))
        {
            errorCode = "SELLER_INVALID";
            return false;
        }

        if (parsedSellerId != expectedSellerId)
        {
            errorCode = "SELLER_MISMATCH_FOR_INTEGRATION";
            return false;
        }

        return true;
    }

    private static bool IsMercadoLivreChannel(string? channel)
    {
        return string.IsNullOrWhiteSpace(channel) ||
               string.Equals(channel.Trim(), MercadoLivreChannel, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<CategoryAutofillResolution?> TryResolveCategoryAutofillAsync(
        string tenantId,
        Guid clientId,
        string variantSku,
        string baseSku,
        string? siteIdRaw,
        string? traceId,
        CancellationToken cancellationToken)
    {
        var normalizedSiteId = string.IsNullOrWhiteSpace(siteIdRaw)
            ? "MLB"
            : siteIdRaw.Trim().ToUpperInvariant();
        var internalCategorySlug = await _dbContext.Products.AsNoTracking()
            .Where(item => item.Sku == baseSku)
            .Select(item => item.CategoryId)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(internalCategorySlug))
        {
            _logger.LogInformation(
                "category_autofill_not_found tenantId={TenantId} clientId={ClientId} variantSku={VariantSku} siteId={SiteId} internalCategorySlug={InternalCategorySlug} mappedCategoryId={MappedCategoryId} traceId={TraceId} reason={Reason}",
                tenantId,
                clientId,
                variantSku,
                normalizedSiteId,
                string.Empty,
                string.Empty,
                traceId ?? string.Empty,
                "internal_category_missing");
            return null;
        }

        var normalizedCategorySlug = internalCategorySlug.Trim().ToLowerInvariant();
        var path = await TryBuildInternalCategoryPathAsync(normalizedCategorySlug, cancellationToken);
        var categoryMappings = _mercadoLivreOptions.CategoryMappings;
        if (categoryMappings == null || categoryMappings.Count == 0)
        {
            _logger.LogInformation(
                "category_autofill_not_found tenantId={TenantId} clientId={ClientId} variantSku={VariantSku} siteId={SiteId} internalCategorySlug={InternalCategorySlug} mappedCategoryId={MappedCategoryId} traceId={TraceId} reason={Reason}",
                tenantId,
                clientId,
                variantSku,
                normalizedSiteId,
                normalizedCategorySlug,
                string.Empty,
                traceId ?? string.Empty,
                "mapping_not_configured");
            return null;
        }

        var siteKey = $"{normalizedSiteId}:{normalizedCategorySlug}";
        string? mappedCategoryIdRaw = null;
        string? mappingSource = null;
        if (categoryMappings.TryGetValue(siteKey, out var siteMappedCategoryId) &&
            !string.IsNullOrWhiteSpace(siteMappedCategoryId))
        {
            mappedCategoryIdRaw = siteMappedCategoryId;
            mappingSource = siteKey;
        }
        else if (categoryMappings.TryGetValue(normalizedCategorySlug, out var mappedCategoryId) &&
                 !string.IsNullOrWhiteSpace(mappedCategoryId))
        {
            mappedCategoryIdRaw = mappedCategoryId;
            mappingSource = normalizedCategorySlug;
        }

        if (string.IsNullOrWhiteSpace(mappedCategoryIdRaw))
        {
            _logger.LogInformation(
                "category_autofill_not_found tenantId={TenantId} clientId={ClientId} variantSku={VariantSku} siteId={SiteId} internalCategorySlug={InternalCategorySlug} mappedCategoryId={MappedCategoryId} traceId={TraceId} reason={Reason}",
                tenantId,
                clientId,
                variantSku,
                normalizedSiteId,
                normalizedCategorySlug,
                string.Empty,
                traceId ?? string.Empty,
                "mapping_key_missing");
            return null;
        }

        if (!TryNormalizeAndValidateCategoryId(
                normalizedSiteId,
                mappedCategoryIdRaw,
                out var validatedSiteId,
                out var normalizedMappedCategoryId))
        {
            _logger.LogWarning(
                "category_autofill_invalid_mapping tenantId={TenantId} clientId={ClientId} variantSku={VariantSku} siteId={SiteId} internalCategorySlug={InternalCategorySlug} mappedCategoryId={MappedCategoryId} traceId={TraceId} mappingSource={MappingSource}",
                tenantId,
                clientId,
                variantSku,
                normalizedSiteId,
                normalizedCategorySlug,
                mappedCategoryIdRaw.Trim(),
                traceId ?? string.Empty,
                mappingSource ?? string.Empty);
            return null;
        }

        _logger.LogInformation(
            "category_autofill_applied tenantId={TenantId} clientId={ClientId} variantSku={VariantSku} siteId={SiteId} internalCategorySlug={InternalCategorySlug} mappedCategoryId={MappedCategoryId} traceId={TraceId} mappingSource={MappingSource}",
            tenantId,
            clientId,
            variantSku,
            validatedSiteId,
            normalizedCategorySlug,
            normalizedMappedCategoryId,
            traceId ?? string.Empty,
            mappingSource ?? string.Empty);
        return new CategoryAutofillResolution
        {
            InternalCategoryPath = path,
            MappingSource = mappingSource ?? string.Empty,
            MappedCategoryId = normalizedMappedCategoryId
        };
    }

    private async Task<string?> TryBuildInternalCategoryPathAsync(string categorySlug, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(categorySlug))
        {
            return null;
        }

        var current = await _dbContext.Categories.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Slug == categorySlug, cancellationToken);
        if (current == null)
        {
            return null;
        }

        var parts = new List<string>();
        var visited = new HashSet<Guid>();
        while (current != null && visited.Add(current.Id))
        {
            if (!string.IsNullOrWhiteSpace(current.Name))
            {
                parts.Add(current.Name.Trim());
            }

            if (!current.ParentId.HasValue)
            {
                break;
            }

            current = await _dbContext.Categories.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == current.ParentId.Value, cancellationToken);
        }

        if (parts.Count == 0)
        {
            return null;
        }

        parts.Reverse();
        return string.Join(" > ", parts);
    }

    private static bool TryNormalizeAndValidateCategoryId(
        string? siteIdRaw,
        string? categoryIdRaw,
        out string normalizedSiteId,
        out string normalizedCategoryId)
    {
        normalizedSiteId = string.IsNullOrWhiteSpace(siteIdRaw)
            ? "MLB"
            : siteIdRaw.Trim().ToUpperInvariant();
        normalizedCategoryId = string.IsNullOrWhiteSpace(categoryIdRaw)
            ? string.Empty
            : categoryIdRaw.Trim().ToUpperInvariant();

        if (!SiteIdRegex.IsMatch(normalizedSiteId))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(normalizedCategoryId))
        {
            return false;
        }

        var categoryRegex = $"^{Regex.Escape(normalizedSiteId)}\\d+$";
        return Regex.IsMatch(normalizedCategoryId, categoryRegex, RegexOptions.CultureInvariant);
    }

    private static string NormalizeCategoryId(string? categoryIdRaw)
    {
        return string.IsNullOrWhiteSpace(categoryIdRaw)
            ? string.Empty
            : categoryIdRaw.Trim().ToUpperInvariant();
    }

    private sealed class CategoryAutofillResolution
    {
        public string? MappedCategoryId { get; set; }
        public string? MappingSource { get; set; }
        public string? InternalCategoryPath { get; set; }
    }

    private sealed class CategoryLockLabel
    {
        public CategoryLockLabel(string categoryName, string? categoryPathFromRoot)
        {
            CategoryName = categoryName;
            CategoryPathFromRoot = categoryPathFromRoot;
        }

        public string CategoryName { get; }
        public string? CategoryPathFromRoot { get; }
    }

    private sealed class LockCacheCategorySuggestCandidate
    {
        public string CategoryId { get; set; } = string.Empty;
        public string CategoryName { get; set; } = string.Empty;
        public string CategoryPath { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAt { get; set; }
        public int Score { get; set; }
    }

    private static string NormalizeSuggestQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        var normalized = string.Join(
            ' ',
            query.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Trim();
    }

    private async Task<MarketplaceCategorySuggestResult> CreateDegradedSuggestResultAsync(
        SuggestDegradedReason reason,
        string tenantId,
        Guid clientId,
        string safeSiteId,
        string query,
        CancellationToken cancellationToken)
    {
        var items = await BuildLockCacheSuggestItemsAsync(
            tenantId,
            clientId,
            safeSiteId,
            query,
            cancellationToken);

        return CreateDegradedSuggestResult(reason, items);
    }

    private async Task<List<MarketplaceCategorySuggestItemResult>> BuildLockCacheSuggestItemsAsync(
        string tenantId,
        Guid clientId,
        string safeSiteId,
        string query,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = NormalizeSuggestQuery(query).ToUpperInvariant();
        var tokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.Length >= 3)
            .Take(8)
            .ToArray();

        var approvedLocks = await _dbContext.ProductMarketplaceCategoryLocks.AsNoTracking()
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.SiteId == safeSiteId
                           && (item.Status == MarketplaceCategoryLockStatus.ApprovedAuto
                               || item.Status == MarketplaceCategoryLockStatus.ApprovedManual))
            .OrderByDescending(item => item.UpdatedAt)
            .Select(item => new
            {
                item.ApprovedCategoryId,
                item.ApprovedCategoryName,
                item.ApprovedCategoryPath,
                item.UpdatedAt
            })
            .Take(250)
            .ToListAsync(cancellationToken);

        var items = approvedLocks
            .Select(item =>
            {
                if (!TryNormalizeAndValidateCategoryId(
                        safeSiteId,
                        item.ApprovedCategoryId,
                        out _,
                        out var normalizedCategoryId))
                {
                    return null;
                }

                var categoryName = string.IsNullOrWhiteSpace(item.ApprovedCategoryName)
                    ? normalizedCategoryId
                    : item.ApprovedCategoryName.Trim();
                var categoryPath = string.IsNullOrWhiteSpace(item.ApprovedCategoryPath)
                    ? categoryName
                    : item.ApprovedCategoryPath!.Trim();
                var searchable = NormalizeSuggestQuery($"{categoryName} {categoryPath}").ToUpperInvariant();
                return new LockCacheCategorySuggestCandidate
                {
                    CategoryId = normalizedCategoryId,
                    CategoryName = categoryName,
                    CategoryPath = categoryPath,
                    UpdatedAt = item.UpdatedAt,
                    Score = ComputeSuggestSearchScore(searchable, normalizedQuery, tokens)
                };
            })
            .OfType<LockCacheCategorySuggestCandidate>()
            .GroupBy(item => item.CategoryId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.UpdatedAt)
                .First())
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.UpdatedAt)
            .Take(5)
            .Select(item => new MarketplaceCategorySuggestItemResult
            {
                CategoryId = item.CategoryId,
                CategoryName = item.CategoryName,
                Source = "lock_cache",
                PathFromRoot = item.CategoryPath
            })
            .ToList();

        return items;
    }

    private static int ComputeSuggestSearchScore(string searchable, string normalizedQuery, IReadOnlyCollection<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(searchable))
        {
            return 0;
        }

        var score = 0;
        if (!string.IsNullOrWhiteSpace(normalizedQuery) && searchable.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 6;
        }

        foreach (var token in tokens)
        {
            if (searchable.Contains(token, StringComparison.Ordinal))
            {
                score += 1;
            }
        }

        return score;
    }

    private static MarketplaceCategorySuggestResult CreateDegradedSuggestResult(
        SuggestDegradedReason reason,
        IReadOnlyCollection<MarketplaceCategorySuggestItemResult>? items = null)
    {
        return new MarketplaceCategorySuggestResult
        {
            Items = items?.ToList() ?? new List<MarketplaceCategorySuggestItemResult>(),
            Degraded = true,
            Reason = reason,
            TraceId = null
        };
    }

    private static bool IsMlAuthInvalidException(Exception ex)
    {
        return ex is InvalidOperationException invalidOperationException
               && string.Equals(invalidOperationException.Message, "ML_AUTH_INVALID", StringComparison.Ordinal);
    }

    private static bool IsUnauthorizedOrForbidden(HttpStatusCode? statusCode)
    {
        return statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.Forbidden;
    }

    private static bool IsUnauthorizedOrForbidden(Exception ex)
    {
        return IsUnauthorizedOrForbidden(GetMlStatusCode(ex));
    }

    private static HttpStatusCode? GetMlStatusCode(Exception ex)
    {
        return ex switch
        {
            MercadoLivreApiException mercadoLivreApiException => mercadoLivreApiException.StatusCode,
            HttpRequestException httpRequestException => httpRequestException.StatusCode,
            _ => null
        };
    }

    private static bool IsMlFeesInputInvalidException(Exception ex)
    {
        var statusCode = GetMlStatusCode(ex);
        return statusCode == HttpStatusCode.BadRequest
               || statusCode == HttpStatusCode.NotFound
               || statusCode == HttpStatusCode.UnprocessableEntity;
    }

    private static bool IsMlCategoryInvalidException(Exception ex)
    {
        var statusCode = GetMlStatusCode(ex);
        return statusCode == HttpStatusCode.BadRequest
               || statusCode == HttpStatusCode.NotFound
               || statusCode == HttpStatusCode.UnprocessableEntity;
    }

    private static bool IsMlPublishInputInvalidException(Exception ex)
    {
        var statusCode = GetMlStatusCode(ex);
        return statusCode == HttpStatusCode.BadRequest
               || statusCode == HttpStatusCode.NotFound
               || statusCode == HttpStatusCode.UnprocessableEntity;
    }

    private static bool ShouldReturnAsHttpFailure(IReadOnlyCollection<ValidationError> errors)
    {
        if (errors.Count == 0)
        {
            return false;
        }

        var code = errors.First().Message;
        return string.Equals(code, "ML_AUTH_INVALID", StringComparison.Ordinal)
               || string.Equals(code, "ML_CATEGORY_INVALID", StringComparison.Ordinal)
               || string.Equals(code, "ML_UNAVAILABLE", StringComparison.Ordinal);
    }

    private static bool IsMlTransientException(Exception ex)
    {
        return ex switch
        {
            TaskCanceledException => true,
            MercadoLivreApiException mercadoLivreApiException => IsMlTransientFailure(mercadoLivreApiException.StatusCode),
            HttpRequestException httpRequestException => IsMlTransientFailure(httpRequestException.StatusCode),
            InvalidOperationException invalidOperationException when string.Equals(
                invalidOperationException.Message,
                "ML_CIRCUIT_OPEN",
                StringComparison.Ordinal) => true,
            _ => false
        };
    }

    private static bool IsCategoryLockStorageUnavailable(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            var sqlState = TryGetPostgresSqlState(current);
            if (string.Equals(sqlState, "42P01", StringComparison.Ordinal)
                || string.Equals(sqlState, "42703", StringComparison.Ordinal))
            {
                return true;
            }

            if (current is InvalidOperationException or DbUpdateException)
            {
                var message = current.Message ?? string.Empty;
                if (message.Contains("product_marketplace_category_lock", StringComparison.OrdinalIgnoreCase)
                    && (message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("no such table", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("invalid object name", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsDraftNaturalKeyUniqueViolation(DbUpdateException ex)
    {
        for (var current = (Exception)ex; current != null; current = current.InnerException)
        {
            var sqlState = TryGetPostgresSqlState(current);
            if (!string.Equals(sqlState, "23505", StringComparison.Ordinal))
            {
                continue;
            }

            var constraintName = TryGetPostgresConstraintName(current);
            if (string.Equals(constraintName, "ux_listing_drafts_scope_integration_variant", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var message = current.Message ?? string.Empty;
            if (message.Contains("ux_listing_drafts_scope_integration_variant", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryGetPostgresSqlState(Exception ex)
    {
        if (!string.Equals(ex.GetType().Name, "PostgresException", StringComparison.Ordinal))
        {
            return null;
        }

        return ex.GetType().GetProperty("SqlState", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(ex)
            ?.ToString();
    }

    private static string? TryGetPostgresConstraintName(Exception ex)
    {
        if (!string.Equals(ex.GetType().Name, "PostgresException", StringComparison.Ordinal))
        {
            return null;
        }

        return ex.GetType().GetProperty("ConstraintName", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(ex)
            ?.ToString();
    }

    private static bool DetachTrackedCategoryLockEntries(DbContext dbContext)
    {
        var entries = dbContext.ChangeTracker.Entries<ProductMarketplaceCategoryLock>()
            .Where(item => item.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        if (entries.Count == 0)
        {
            return false;
        }

        foreach (var entry in entries)
        {
            entry.State = EntityState.Detached;
        }

        return true;
    }

    private async Task<ServiceResult<ListingDraftResult>?> TryRecoverUpsertNaturalKeyRaceAsync(
        string tenantId,
        Guid clientId,
        Guid integrationId,
        string variantSku,
        ListingDraft? trackedDraft,
        string? traceId,
        CancellationToken cancellationToken)
    {
        var normalizedVariantSku = Sku.Normalize(variantSku);
        var efDbContext = _dbContext as DbContext;
        if (efDbContext != null && trackedDraft != null)
        {
            var trackedEntry = efDbContext.Entry(trackedDraft);
            if (trackedEntry.State == EntityState.Added)
            {
                trackedEntry.State = EntityState.Detached;
            }
        }

        var recoveredDraft = await _dbContext.ListingDrafts.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.MercadoLivre
                    && item.IntegrationId == integrationId
                    && item.SabrVariantSku == normalizedVariantSku,
            cancellationToken);
        if (recoveredDraft == null)
        {
            return null;
        }

        _logger.LogInformation(
            "listing_draft_upsert_unique_race_recovered tenantId={TenantId} clientId={ClientId} draftId={DraftId} integrationId={IntegrationId} variantSku={VariantSku} traceId={TraceId}",
            tenantId,
            clientId,
            recoveredDraft.DraftId,
            integrationId,
            normalizedVariantSku,
            traceId ?? string.Empty);
        var rowVersion = GetRowVersion(recoveredDraft);
        return ServiceResult<ListingDraftResult>.Success(MapListingDraftResult(recoveredDraft, rowVersion));
    }

    private static bool IsSuggestTransientFailure(HttpStatusCode? statusCode)
    {
        if (!statusCode.HasValue)
        {
            return true;
        }

        return statusCode == HttpStatusCode.RequestTimeout
               || statusCode == HttpStatusCode.TooManyRequests
               || (int)statusCode.Value >= 500;
    }

    private static bool IsMlTransientFailure(HttpStatusCode? statusCode)
    {
        if (!statusCode.HasValue)
        {
            return true;
        }

        return statusCode == HttpStatusCode.RequestTimeout
               || statusCode == HttpStatusCode.TooManyRequests
               || (int)statusCode.Value >= 500;
    }

    private static MarketplaceFeesEstimateResult BuildFeesEstimateResult(
        Guid integrationId,
        long sellerId,
        string categoryId,
        string listingTypeId,
        string currency,
        decimal normalizedPrice,
        decimal productCost,
        decimal operationalCost,
        MercadoLivreFeeEstimateResponse feeResponse,
        string source)
    {
        var estimatedProfit = normalizedPrice - feeResponse.TotalFeeAmount - productCost - operationalCost;
        decimal? marginPercent = normalizedPrice == 0
            ? null
            : Math.Round((estimatedProfit / normalizedPrice) * 100m, 2, MidpointRounding.AwayFromZero);

        return new MarketplaceFeesEstimateResult
        {
            IntegrationId = integrationId,
            SellerId = MercadoLivreSellerIdParser.ToApiString(sellerId),
            CategoryId = categoryId,
            ListingTypeId = listingTypeId,
            CurrencyId = currency,
            Price = normalizedPrice,
            SaleFee = feeResponse.SaleFeeAmount,
            FixedFee = feeResponse.FixedFeeAmount,
            TotalFees = feeResponse.TotalFeeAmount,
            ProductCost = productCost,
            OperationalCost = operationalCost,
            EstimatedProfit = Math.Round(estimatedProfit, 2, MidpointRounding.AwayFromZero),
            MarginPercent = marginPercent,
            Source = source
        };
    }

    private string GetRowVersion(ListingDraft draft)
    {
        if (_isNpgsqlProvider)
        {
            var xmin = (_dbContext as DbContext)?.Entry(draft).Property<uint>("xmin").CurrentValue ?? 0;
            return ListingDraftHelpers.EncodeRowVersion(xmin, draft.UpdatedAt, true);
        }

        return ListingDraftHelpers.EncodeRowVersion(0, draft.UpdatedAt, false);
    }

    private static string ResolvePermalink(string? itemId, string? permalink)
    {
        if (!string.IsNullOrWhiteSpace(permalink))
        {
            return permalink.Trim();
        }

        if (string.IsNullOrWhiteSpace(itemId))
        {
            return string.Empty;
        }

        return $"https://produto.mercadolivre.com.br/{Uri.EscapeDataString(itemId)}";
    }

    private static string ResolveApiUrl(string? itemId, string? apiUrl)
    {
        if (!string.IsNullOrWhiteSpace(apiUrl))
        {
            if (Uri.TryCreate(apiUrl, UriKind.Absolute, out var absolute))
            {
                return absolute.ToString();
            }

            if (apiUrl.StartsWith("/", StringComparison.Ordinal))
            {
                return $"https://api.mercadolibre.com{apiUrl}";
            }

            return apiUrl;
        }

        if (string.IsNullOrWhiteSpace(itemId))
        {
            return string.Empty;
        }

        return $"https://api.mercadolibre.com/items/{Uri.EscapeDataString(itemId)}";
    }

    private static ServiceResult<T> Failure<T>(string field, string code)
    {
        return ServiceResult<T>.Failure(new[] { new ValidationError(field, code) });
    }

    private sealed record VariantResolutionContext(
        string BaseSku,
        List<ProductVariant> Variants);

    private sealed class PublishValidationContext
    {
        public TenantMarketplaceConnection Connection { get; set; } = null!;
        public Product Product { get; set; } = null!;
        public IReadOnlyList<ProductVariant> Variants { get; set; } = Array.Empty<ProductVariant>();
        public string Title { get; set; } = string.Empty;
        public IReadOnlyList<string> PictureUrls { get; set; } = Array.Empty<string>();
        public IReadOnlyList<MercadoLivreCreateItemAttributeRequest> ItemAttributes { get; set; } = Array.Empty<MercadoLivreCreateItemAttributeRequest>();
        public ProviderDraftData ProviderDraft { get; set; } = new();
        public bool IsMultiVariation { get; set; }
    }

    private sealed class ProviderDraftData
    {
        public string Channel { get; set; } = MercadoLivreChannel;
        public string SiteId { get; set; } = "MLB";
        public string Condition { get; set; } = "new";
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Gtin { get; set; }
        public string? EmptyGtinReason { get; set; }
        public string? Ncm { get; set; }
        public string? Origin { get; set; }
        public List<ProviderDraftImage> Images { get; set; } = new();
        public List<ProviderDraftAttribute> Attributes { get; set; } = new();
        public string PublishMode { get; set; } = PublishModeSingleVariant;
        public List<string> SelectedVariantSkus { get; set; } = new();
        public List<string> VariationAxes { get; set; } = new();
        public List<ProviderDraftVariation> Variations { get; set; } = new();
        public long? ProductCostCents { get; set; }
        public long? OperationalCostCents { get; set; }
        public DateTimeOffset? LastPublishAttemptAtUtc { get; set; }
        public List<ProviderDraftPublishResult> LastPublishResults { get; set; } = new();
    }

    private sealed class ProviderDraftVariation
    {
        public string SabrVariantSku { get; set; } = string.Empty;
        public decimal? Price { get; set; }
        public int? InitialQuantity { get; set; }
        public List<ProviderDraftVariationAttribute> Attributes { get; set; } = new();
        public List<string> PictureIds { get; set; } = new();
    }

    private sealed class ProviderDraftVariationAttribute
    {
        public string Id { get; set; } = string.Empty;
        public string? ValueId { get; set; }
        public string? ValueName { get; set; }
    }

    private sealed class ProviderDraftImage
    {
        public string Url { get; set; } = string.Empty;
        public int Position { get; set; }
    }

    private sealed class ProviderDraftAttribute
    {
        public string Id { get; set; } = string.Empty;
        public string? ValueId { get; set; }
        public string? ValueName { get; set; }
    }

    private sealed class ProviderDraftPublishResult
    {
        public string SabrVariantSku { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? VariationId { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTimeOffset? PublishedAtUtc { get; set; }
    }
}
