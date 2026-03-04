using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;
using Sabr.Domain.ValueObjects;

namespace Sabr.Application.Categories;

public enum CategoryResolutionStatus
{
    Ready,
    SelectionRequired,
    ReviewRequired
}

public sealed class MarketplaceCategoryResolverRequest
{
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public string BaseProductSku { get; set; } = string.Empty;
    public string SiteId { get; set; } = "MLB";
    public string? Query { get; set; }
    public string? DraftCategoryId { get; set; }
    public string? DraftSiteId { get; set; }
    public string? AccessToken { get; set; }
    public string? TraceId { get; set; }
}

public sealed class CategorySuggestionOption
{
    public string CategoryId { get; set; } = string.Empty;
    public string? CategoryName { get; set; }
    public string? CategoryPathFromRoot { get; set; }
    public string Source { get; set; } = "mapping";
    public int Rank { get; set; }
}

public sealed class MarketplaceCategoryResolutionResult
{
    public string SiteId { get; set; } = "MLB";
    public string? InternalCategorySlug { get; set; }
    public string? InternalCategoryPath { get; set; }
    public string? SuggestedCategoryId { get; set; }
    public string? SuggestedCategorySource { get; set; }
    public string? SuggestedCategoryPath { get; set; }
    public CategoryResolutionStatus ResolutionStatus { get; set; } = CategoryResolutionStatus.SelectionRequired;
    public bool CategorySelectionRequired { get; set; } = true;
    public string CategoryResolutionReason { get; set; } = "SELECTION_REQUIRED_MULTIPLE_MATCHES";
    public bool DriftDetected { get; set; }
    public bool HighConfidence { get; set; }
    public bool LockRequiresReview { get; set; }
    public bool CategoryLockAvailable { get; set; } = true;
    public List<CategorySuggestionOption> Suggestions { get; set; } = new();
}

public sealed class MarketplaceCategoryResolver
{
    private readonly IAppDbContext _dbContext;
    private readonly IMercadoLivreApiClient _mercadoLivreApiClient;
    private readonly ILogger<MarketplaceCategoryResolver> _logger;

    public MarketplaceCategoryResolver(
        IAppDbContext dbContext,
        IMercadoLivreApiClient mercadoLivreApiClient,
        ILogger<MarketplaceCategoryResolver> logger)
    {
        _dbContext = dbContext;
        _mercadoLivreApiClient = mercadoLivreApiClient;
        _logger = logger;
    }

    public async Task<MarketplaceCategoryResolutionResult> ResolveAsync(
        MarketplaceCategoryResolverRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedSiteId = NormalizeSiteId(request.SiteId);
        var normalizedBaseSku = Sku.Normalize(request.BaseProductSku);
        var normalizedDraftCategoryId = NormalizeCategoryId(request.DraftCategoryId);
        var normalizedDraftSiteId = NormalizeSiteId(string.IsNullOrWhiteSpace(request.DraftSiteId) ? normalizedSiteId : request.DraftSiteId);
        var traceId = request.TraceId ?? string.Empty;

        _logger.LogInformation(
            "category_resolve_started tenantId={TenantId} clientId={ClientId} baseSku={BaseSku} siteId={SiteId} traceId={TraceId}",
            request.TenantId,
            request.ClientId,
            normalizedBaseSku,
            normalizedSiteId,
            traceId);

        var product = await _dbContext.Products.AsNoTracking()
            .Where(item => item.Sku == normalizedBaseSku)
            .Select(item => new
            {
                item.Name,
                item.CategoryId
            })
            .FirstOrDefaultAsync(cancellationToken);

        var internalCategorySlug = (product?.CategoryId ?? string.Empty).Trim().ToLowerInvariant();
        if (internalCategorySlug.Length == 0)
        {
            internalCategorySlug = null;
        }

        var internalCategoryPath = internalCategorySlug == null
            ? null
            : await TryBuildInternalCategoryPathAsync(internalCategorySlug, cancellationToken);

        var categoryLockAvailable = true;
        ProductMarketplaceCategoryLock? lockEntity = null;
        try
        {
            lockEntity = await _dbContext.ProductMarketplaceCategoryLocks.AsNoTracking()
                .FirstOrDefaultAsync(
                    item => item.TenantId == request.TenantId
                            && item.ClientId == request.ClientId
                            && item.BaseProductSku == normalizedBaseSku
                            && item.SiteId == normalizedSiteId,
                    cancellationToken);
        }
        catch (Exception ex) when (IsCategoryLockStorageUnavailable(ex))
        {
            categoryLockAvailable = false;
            _logger.LogWarning(
                ex,
                "category_lock_unavailable tenantId={TenantId} clientId={ClientId} baseSku={BaseSku} siteId={SiteId} action={Action} traceId={TraceId}",
                request.TenantId,
                request.ClientId,
                normalizedBaseSku,
                normalizedSiteId,
                "read",
                traceId);
        }

        var lockStatusApproved = lockEntity != null && IsApprovedLockStatus(lockEntity.Status);
        var lockRequiresReviewBySlug = lockEntity != null
                                       && lockStatusApproved
                                       && !string.Equals(
                                           NormalizeCategorySlug(lockEntity.InternalCategorySlugSnapshot),
                                           internalCategorySlug,
                                           StringComparison.OrdinalIgnoreCase);

        var candidates = new List<CategoryCandidate>();
        var mlUnavailable = false;
        var mlAuthInvalid = false;
        var discoveryQuery = BuildDiscoveryQuery(request.Query, product?.Name, normalizedBaseSku);
        if (string.IsNullOrWhiteSpace(request.AccessToken))
        {
            mlUnavailable = true;
        }
        else if (discoveryQuery.Length >= 3)
        {
            try
            {
                var discovery = await _mercadoLivreApiClient.SuggestCategoriesByDomainDiscoveryAsync(
                    normalizedSiteId,
                    discoveryQuery,
                    request.AccessToken!,
                    cancellationToken);

                var rank = 1;
                foreach (var item in discovery.Where(item => !string.IsNullOrWhiteSpace(item.CategoryId)).Take(3))
                {
                    candidates.Add(new CategoryCandidate
                    {
                        CategoryId = item.CategoryId,
                        CategoryName = item.CategoryName,
                        CategoryPath = item.PathFromRoot,
                        Source = "domain_discovery",
                        Priority = rank,
                        Rank = rank
                    });
                    rank++;
                }
            }
            catch (Exception ex)
            {
                if (IsMlAuthInvalidException(ex) || IsUnauthorizedOrForbidden(ex))
                {
                    mlAuthInvalid = true;
                }
                else if (ex is HttpRequestException badReq && badReq.StatusCode == HttpStatusCode.BadRequest)
                {
                    _logger.LogWarning(
                        "category_resolve_ml_bad_request_ignored tenantId={TenantId} clientId={ClientId} baseSku={BaseSku} siteId={SiteId} traceId={TraceId}",
                        request.TenantId,
                        request.ClientId,
                        normalizedBaseSku,
                        normalizedSiteId,
                        traceId);
                }
                else
                {
                    mlUnavailable = true;
                    _logger.LogWarning(
                        ex,
                        "category_resolve_ml_unavailable_blocked tenantId={TenantId} clientId={ClientId} baseSku={BaseSku} siteId={SiteId} traceId={TraceId}",
                        request.TenantId,
                        request.ClientId,
                        normalizedBaseSku,
                        normalizedSiteId,
                        traceId);
                }
            }
        }

        var distinctCandidates = candidates
            .Select(candidate =>
            {
                candidate.CategoryId = NormalizeCategoryId(candidate.CategoryId);
                return candidate;
            })
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.CategoryId))
            .GroupBy(candidate => candidate.CategoryId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(candidate => candidate.Priority)
                .ThenBy(candidate => candidate.Rank)
                .First())
            .OrderBy(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Rank)
            .ToList();

        var capabilitiesByCategoryId = new Dictionary<string, MercadoLivreCategoryCapabilityResponse>(StringComparer.OrdinalIgnoreCase);
        var validCandidates = new List<CategoryCandidate>();
        foreach (var candidate in distinctCandidates)
        {
            if (!TryNormalizeAndValidateCategoryId(normalizedSiteId, candidate.CategoryId, out var normalizedCandidateId))
            {
                continue;
            }

            candidate.CategoryId = normalizedCandidateId;
            if (string.IsNullOrWhiteSpace(request.AccessToken))
            {
                continue;
            }

            MercadoLivreCategoryCapabilityResponse? capability = null;
            if (!capabilitiesByCategoryId.TryGetValue(candidate.CategoryId, out capability))
            {
                try
                {
                    capability = await _mercadoLivreApiClient.GetCategoryCapabilitiesAsync(candidate.CategoryId, request.AccessToken!, cancellationToken);
                    capabilitiesByCategoryId[candidate.CategoryId] = capability;
                }
                catch (Exception ex)
                {
                    if (IsMlAuthInvalidException(ex) || IsUnauthorizedOrForbidden(ex))
                    {
                        mlAuthInvalid = true;
                    }
                    else if (ex is HttpRequestException badReq && badReq.StatusCode == HttpStatusCode.BadRequest)
                    {
                        _logger.LogWarning(
                            "category_resolve_capabilities_bad_request_ignored tenantId={TenantId} clientId={ClientId} baseSku={BaseSku} siteId={SiteId} categoryId={CategoryId} traceId={TraceId}",
                            request.TenantId,
                            request.ClientId,
                            normalizedBaseSku,
                            normalizedSiteId,
                            candidate.CategoryId,
                            traceId);
                    }
                    else
                    {
                        mlUnavailable = true;
                        _logger.LogWarning(
                            ex,
                            "category_resolve_ml_unavailable_blocked tenantId={TenantId} clientId={ClientId} baseSku={BaseSku} siteId={SiteId} categoryId={CategoryId} traceId={TraceId}",
                            request.TenantId,
                            request.ClientId,
                            normalizedBaseSku,
                            normalizedSiteId,
                            candidate.CategoryId,
                            traceId);
                    }
                    continue;
                }
            }

            if (capability == null || !capability.IsLeaf)
            {
                continue;
            }

            candidate.CategoryName = FirstNonEmpty(capability.CategoryName, candidate.CategoryName);
            candidate.CategoryPath = FirstNonEmpty(capability.CategoryPathFromRoot, candidate.CategoryPath);
            validCandidates.Add(candidate);
        }

        var recommended = validCandidates.FirstOrDefault();
        var highConfidence = recommended != null && validCandidates.Count == 1;
        var lockRequiresReviewByMlDrift = lockEntity != null
                                          && lockStatusApproved
                                          && recommended != null
                                          && !string.IsNullOrWhiteSpace(lockEntity.ApprovedCategoryId)
                                          && !string.Equals(
                                              NormalizeCategoryId(lockEntity.ApprovedCategoryId),
                                              recommended.CategoryId,
                                              StringComparison.OrdinalIgnoreCase);
        var lockRequiresReview = lockRequiresReviewBySlug || lockRequiresReviewByMlDrift;
        if (lockRequiresReviewByMlDrift)
        {
            _logger.LogInformation(
                "category_lock_marked_review_due_to_ml_drift tenantId={TenantId} clientId={ClientId} baseSku={BaseSku} siteId={SiteId} lockCategoryId={LockCategoryId} mlSuggestedCategoryId={MlSuggestedCategoryId} traceId={TraceId}",
                request.TenantId,
                request.ClientId,
                normalizedBaseSku,
                normalizedSiteId,
                NormalizeCategoryId(lockEntity?.ApprovedCategoryId),
                recommended?.CategoryId ?? string.Empty,
                traceId);
        }

        var hasDraft = !string.IsNullOrWhiteSpace(normalizedDraftCategoryId);
        var hasDraftDrift = hasDraft
                            && recommended != null
                            && string.Equals(normalizedDraftSiteId, normalizedSiteId, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(normalizedDraftCategoryId, recommended.CategoryId, StringComparison.OrdinalIgnoreCase);

        var suggestions = validCandidates
            .Take(3)
            .Select((item, index) => new CategorySuggestionOption
            {
                CategoryId = item.CategoryId,
                CategoryName = item.CategoryName,
                CategoryPathFromRoot = item.CategoryPath,
                Source = item.Source,
                Rank = index + 1
            })
            .ToList();

        var mlResolutionUnavailable = (mlUnavailable || mlAuthInvalid) && recommended == null;
        if (mlResolutionUnavailable && suggestions.Count == 0)
        {
            suggestions = await BuildFallbackSuggestionsFromLocksAsync(
                request.TenantId,
                request.ClientId,
                normalizedSiteId,
                discoveryQuery,
                cancellationToken);
            if (suggestions.Count > 0)
            {
                _logger.LogInformation(
                    "category_resolve_ml_unavailable_fallback_local tenantId={TenantId} clientId={ClientId} baseSku={BaseSku} siteId={SiteId} reason={Reason} suggestions={SuggestionsCount} traceId={TraceId}",
                    request.TenantId,
                    request.ClientId,
                    normalizedBaseSku,
                    normalizedSiteId,
                    mlAuthInvalid ? "ML_AUTH_INVALID" : "ML_UNAVAILABLE",
                    suggestions.Count,
                    traceId);
            }
        }

        var status = CategoryResolutionStatus.SelectionRequired;
        if (!mlResolutionUnavailable && (lockRequiresReview || hasDraftDrift))
        {
            status = CategoryResolutionStatus.ReviewRequired;
        }
        else if (recommended != null && highConfidence)
        {
            status = CategoryResolutionStatus.Ready;
        }

        var categoryResolutionReason = "SELECTION_REQUIRED_MULTIPLE_MATCHES";
        if (status == CategoryResolutionStatus.ReviewRequired)
        {
            categoryResolutionReason = "REVIEW_REQUIRED_STALE_DRAFT";
        }
        else if (status == CategoryResolutionStatus.Ready)
        {
            categoryResolutionReason = "READY_SINGLE_MATCH";
        }
        else if (mlAuthInvalid && recommended == null)
        {
            categoryResolutionReason = "ML_AUTH_INVALID";
        }
        else if (mlUnavailable && recommended == null)
        {
            categoryResolutionReason = "ML_UNAVAILABLE";
        }

        var result = new MarketplaceCategoryResolutionResult
        {
            SiteId = normalizedSiteId,
            InternalCategorySlug = internalCategorySlug,
            InternalCategoryPath = internalCategoryPath,
            SuggestedCategoryId = recommended?.CategoryId,
            SuggestedCategorySource = recommended?.SourceDetail ?? recommended?.Source,
            SuggestedCategoryPath = recommended?.CategoryPath ?? recommended?.CategoryName,
            ResolutionStatus = status,
            CategorySelectionRequired = status != CategoryResolutionStatus.Ready,
            CategoryResolutionReason = categoryResolutionReason,
            DriftDetected = hasDraftDrift,
            HighConfidence = highConfidence,
            LockRequiresReview = lockRequiresReview,
            CategoryLockAvailable = categoryLockAvailable,
            Suggestions = suggestions
        };

        _logger.LogInformation(
            "category_resolve_ml_source_of_truth tenantId={TenantId} clientId={ClientId} baseSku={BaseSku} siteId={SiteId} status={Status} reason={Reason} suggestedCategoryId={SuggestedCategoryId} highConfidence={HighConfidence} drift={DriftDetected} lockRequiresReview={LockRequiresReview} suggestions={SuggestionsCount} traceId={TraceId}",
            request.TenantId,
            request.ClientId,
            normalizedBaseSku,
            normalizedSiteId,
            result.ResolutionStatus.ToString(),
            result.CategoryResolutionReason,
            result.SuggestedCategoryId ?? string.Empty,
            result.HighConfidence,
            result.DriftDetected,
            result.LockRequiresReview,
            result.Suggestions.Count,
            traceId);

        return result;
    }

    private async Task<List<CategorySuggestionOption>> BuildFallbackSuggestionsFromLocksAsync(
        string tenantId,
        Guid clientId,
        string siteId,
        string? query,
        CancellationToken cancellationToken)
    {
        var approvedLocks = await _dbContext.ProductMarketplaceCategoryLocks.AsNoTracking()
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.SiteId == siteId
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

        if (approvedLocks.Count == 0)
        {
            return new List<CategorySuggestionOption>();
        }

        var normalizedQuery = NormalizeSearchText(query);
        var queryTokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 3)
            .Take(8)
            .ToArray();

        var localSuggestions = approvedLocks
            .Select(item =>
            {
                if (!TryNormalizeAndValidateCategoryId(siteId, item.ApprovedCategoryId, out var normalizedCategoryId))
                {
                    return null;
                }

                var categoryName = string.IsNullOrWhiteSpace(item.ApprovedCategoryName)
                    ? normalizedCategoryId
                    : item.ApprovedCategoryName.Trim();
                var categoryPath = string.IsNullOrWhiteSpace(item.ApprovedCategoryPath)
                    ? categoryName
                    : item.ApprovedCategoryPath!.Trim();
                var searchable = NormalizeSearchText($"{categoryName} {categoryPath}");
                return new FallbackCategorySuggestionCandidate
                {
                    CategoryId = normalizedCategoryId,
                    CategoryName = categoryName,
                    CategoryPath = categoryPath,
                    UpdatedAt = item.UpdatedAt,
                    Score = ComputeSearchScore(searchable, normalizedQuery, queryTokens)
                };
            })
            .Where(item => item != null)
            .GroupBy(item => item!.CategoryId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item!.Score)
                .ThenByDescending(item => item!.UpdatedAt)
                .First()!)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.UpdatedAt)
            .Take(5)
            .ToList();

        return localSuggestions
            .Select((item, index) => new CategorySuggestionOption
            {
                CategoryId = item.CategoryId,
                CategoryName = item.CategoryName,
                CategoryPathFromRoot = item.CategoryPath,
                Source = "lock_cache",
                Rank = index + 1
            })
            .ToList();
    }

    private static int ComputeSearchScore(string searchable, string normalizedQuery, IReadOnlyCollection<string> queryTokens)
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

        foreach (var token in queryTokens)
        {
            if (searchable.Contains(token, StringComparison.Ordinal))
            {
                score += 1;
            }
        }

        return score;
    }

    private static string NormalizeSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        return string.Join(
            ' ',
            builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool IsMlAuthInvalidException(Exception ex)
    {
        return ex is InvalidOperationException invalidOperationException
               && string.Equals(invalidOperationException.Message, "ML_AUTH_INVALID", StringComparison.Ordinal);
    }

    private static bool IsUnauthorizedOrForbidden(Exception ex)
    {
        var statusCode = ex switch
        {
            MercadoLivreApiException mercadoLivreApiException => mercadoLivreApiException.StatusCode,
            HttpRequestException httpRequestException => httpRequestException.StatusCode,
            _ => null
        };
        return statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.Forbidden;
    }

    private async Task<string?> TryBuildInternalCategoryPathAsync(string categorySlug, CancellationToken cancellationToken)
    {
        var current = await _dbContext.Categories.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Slug == categorySlug, cancellationToken);
        if (current == null)
        {
            return null;
        }

        var visited = new HashSet<Guid>();
        var path = new List<string>();
        while (current != null && visited.Add(current.Id))
        {
            if (!string.IsNullOrWhiteSpace(current.Name))
            {
                path.Add(current.Name.Trim());
            }

            if (!current.ParentId.HasValue)
            {
                break;
            }

            current = await _dbContext.Categories.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == current.ParentId.Value, cancellationToken);
        }

        if (path.Count == 0)
        {
            return null;
        }

        path.Reverse();
        return string.Join(" > ", path);
    }

    private static bool IsApprovedLockStatus(MarketplaceCategoryLockStatus status)
    {
        return status == MarketplaceCategoryLockStatus.ApprovedAuto || status == MarketplaceCategoryLockStatus.ApprovedManual;
    }

    private static string BuildDiscoveryQuery(string? requestQuery, string? productName, string baseSku)
    {
        var query = (requestQuery ?? string.Empty).Trim();
        if (query.Length >= 3)
        {
            return query;
        }

        query = (productName ?? string.Empty).Trim();
        if (query.Length >= 3)
        {
            return query;
        }

        return baseSku;
    }

    private static string NormalizeCategorySlug(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    }

    private static string NormalizeSiteId(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return SiteIdRegex().IsMatch(normalized) ? normalized : "MLB";
    }

    private static string NormalizeCategoryId(string? value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static bool TryNormalizeAndValidateCategoryId(string siteId, string? categoryIdRaw, out string normalizedCategoryId)
    {
        normalizedCategoryId = NormalizeCategoryId(categoryIdRaw);
        if (normalizedCategoryId.Length == 0)
        {
            return false;
        }

        var regex = new Regex($"^{Regex.Escape(siteId)}\\d+$", RegexOptions.CultureInvariant);
        if (!regex.IsMatch(normalizedCategoryId))
        {
            return false;
        }

        return true;
    }

    private static Regex SiteIdRegex()
    {
        return new Regex("^ML[A-Z]$", RegexOptions.CultureInvariant);
    }

    private static string? FirstNonEmpty(string? first, string? second)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first.Trim();
        }

        if (!string.IsNullOrWhiteSpace(second))
        {
            return second.Trim();
        }

        return null;
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

    private sealed class CategoryCandidate
    {
        public string CategoryId { get; set; } = string.Empty;
        public string? CategoryName { get; set; }
        public string? CategoryPath { get; set; }
        public string Source { get; set; } = "mapping";
        public string? SourceDetail { get; set; }
        public int Priority { get; set; }
        public int Rank { get; set; }
    }

    private sealed class FallbackCategorySuggestionCandidate
    {
        public string CategoryId { get; set; } = string.Empty;
        public string CategoryName { get; set; } = string.Empty;
        public string CategoryPath { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAt { get; set; }
        public int Score { get; set; }
    }
}
