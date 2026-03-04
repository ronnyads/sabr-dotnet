using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Stock;
using Sabr.Application.Validation;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;
using Sabr.Domain.ValueObjects;

namespace Sabr.Application.Services;

public sealed class MyProductsService
{
    private const string IdempotencyScope = "my-products:add";

    private readonly IAppDbContext _dbContext;
    private readonly CatalogAuthorizationService _catalogAuthorizationService;
    private readonly PriceCalculator _priceCalculator;
    private readonly bool _isNpgsqlProvider;

    public MyProductsService(
        IAppDbContext dbContext,
        CatalogAuthorizationService catalogAuthorizationService,
        PriceCalculator priceCalculator)
    {
        _dbContext = dbContext;
        _catalogAuthorizationService = catalogAuthorizationService;
        _priceCalculator = priceCalculator;
        _isNpgsqlProvider = string.Equals(
            (dbContext as DbContext)?.Database.ProviderName,
            "Npgsql.EntityFrameworkCore.PostgreSQL",
            StringComparison.Ordinal);
    }

    public async Task<ServiceResult<AddMyProductOperationResult>> AddToMyProductsAsync(
        AddMyProductRequest request,
        string tenantId,
        Guid clientId,
        Guid userId,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateAddRequest(request, tenantId, clientId, userId);
        if (errors.Count > 0)
        {
            return ServiceResult<AddMyProductOperationResult>.Failure(errors);
        }

        var normalizedSku = Sku.Normalize(request.ProductSku);
        var mode = request.PricingMode ?? PricingMode.CatalogPrice;
        var requestHash = HashAddRequest(tenantId, clientId, normalizedSku, mode, request.MarkupPercent, request.FixedPriceCents);
        IdempotencyKey? idempotency = null;

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var key = idempotencyKey.Trim();
            var idempotencyOutcome = await TryAcquireIdempotencyAsync(tenantId, key, requestHash, cancellationToken);
            if (!idempotencyOutcome.Allowed)
            {
                return ServiceResult<AddMyProductOperationResult>.Failure(idempotencyOutcome.Errors);
            }

            if (idempotencyOutcome.CachedResult != null)
            {
                idempotencyOutcome.CachedResult.FromIdempotencyCache = true;
                return ServiceResult<AddMyProductOperationResult>.Success(idempotencyOutcome.CachedResult);
            }

            idempotency = idempotencyOutcome.Entity;
        }

        try
        {
            var result = await AddCoreAsync(
                tenantId,
                clientId,
                userId,
                normalizedSku,
                mode,
                request.MarkupPercent,
                request.FixedPriceCents,
                cancellationToken);

            if (idempotency != null)
            {
                idempotency.Status = result.Succeeded ? IdempotencyStatus.Completed : IdempotencyStatus.Failed;
                idempotency.ResponseJson = result.Succeeded && result.Data != null ? JsonSerializer.Serialize(result.Data) : null;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
        catch
        {
            if (idempotency != null)
            {
                idempotency.Status = IdempotencyStatus.Failed;
                idempotency.ResponseJson = null;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            throw;
        }
    }

    public async Task<PagedResult<MyProductDraftResult>> ListMyProductsAsync(
        string tenantId,
        Guid clientId,
        int skip,
        int limit,
        string? search,
        string? variantSku = null,
        IReadOnlyCollection<string>? variantSkus = null,
        CancellationToken cancellationToken = default)
    {
        var query = BuildDraftProjectionQuery(tenantId, clientId);

        var exactSkuFilter = await ResolveExactSkuFilterAsync(variantSku, variantSkus, cancellationToken);
        if (exactSkuFilter != null)
        {
            if (exactSkuFilter.Count == 0)
            {
                return new PagedResult<MyProductDraftResult>
                {
                    Items = new List<MyProductDraftResult>(),
                    Total = 0,
                    Skip = skip,
                    Limit = limit
                };
            }

            query = query.Where(item => exactSkuFilter.Contains(item.ProductSku));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            query = query.Where(item =>
                item.ProductSku.ToUpper().Contains(term) ||
                item.ProductName.ToUpper().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var projections = await query
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Skip(skip)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var imagesBySku = await LoadImagesBySkuAsync(
            projections.Select(item => item.ProductSku).ToList(),
            cancellationToken);
        var resolvedStockBySku = await LoadResolvedVariantStocksBySkuAsync(
            projections.Select(item => item.ProductSku).ToList(),
            cancellationToken);

        var badgeByBaseSku = await LoadMlBadgeSummaryAsync(
            tenantId,
            clientId,
            projections.Select(item => item.ProductSku).ToList(),
            cancellationToken);

        var mapped = projections.Select(item =>
            MapDraftProjection(
                item,
                imagesBySku.TryGetValue(item.ProductSku, out var images)
                    ? images
                    : Array.Empty<MyProductImageResult>(),
                resolvedStockBySku.TryGetValue(item.ProductSku, out var resolvedStock) ? resolvedStock : null))
            .ToList();
        foreach (var item in mapped)
        {
            if (badgeByBaseSku.TryGetValue(item.ProductSku, out var badge))
            {
                item.MlPublishedCount = badge.PublishedCount;
                item.MlDraftCount = badge.DraftCount;
                item.MlErrorCount = badge.ErrorCount;
                item.MlOverallStatus = badge.OverallStatus;
            }
        }

        return new PagedResult<MyProductDraftResult>
        {
            Items = mapped,
            Total = total,
            Skip = skip,
            Limit = limit
        };
    }

    public async Task<MyProductDraftResult?> GetMyProductDraftByIdAsync(
        Guid draftId,
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var projection = await BuildDraftProjectionQuery(tenantId, clientId)
            .FirstOrDefaultAsync(item => item.Id == draftId, cancellationToken);

        if (projection == null)
        {
            return null;
        }

        var imagesBySku = await LoadImagesBySkuAsync(new[] { projection.ProductSku }, cancellationToken);
        var resolvedStockBySku = await LoadResolvedVariantStocksBySkuAsync(new[] { projection.ProductSku }, cancellationToken);
        return MapDraftProjection(
            projection,
            imagesBySku.TryGetValue(projection.ProductSku, out var images)
                ? images
                : Array.Empty<MyProductImageResult>(),
            resolvedStockBySku.TryGetValue(projection.ProductSku, out var resolvedStock) ? resolvedStock : null);
    }

    public async Task<ServiceResult<MyProductDraftResult>> UpdateMyProductDraftAsync(
        Guid draftId,
        UpdateMyProductDraftRequest request,
        string tenantId,
        Guid clientId,
        Guid userId,
        string? ifMatch,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateUpdateRequest(request, tenantId, clientId, userId);
        if (errors.Count > 0)
        {
            return ServiceResult<MyProductDraftResult>.Failure(errors);
        }

        var publication = await _dbContext.Publications
            .FirstOrDefaultAsync(
                item => item.Id == draftId
                        && item.TenantId == tenantId
                        && item.ClientId == clientId
                        && item.Status == PublicationStatus.Draft,
                cancellationToken);

        if (publication == null)
        {
            return ServiceResult<MyProductDraftResult>.Failure(new[]
            {
                new ValidationError("draftId", "Draft not found")
            });
        }

        var currentRowVersion = GetRowVersion((DbContext)_dbContext, publication);
        var expectedRowVersion = ResolveExpectedRowVersion(ifMatch, request.RowVersion);
        if (string.IsNullOrWhiteSpace(expectedRowVersion))
        {
            return ServiceResult<MyProductDraftResult>.Failure(new[]
            {
                new ValidationError("precondition", "If-Match header or rowVersion is required")
            });
        }

        if (!string.Equals(expectedRowVersion, currentRowVersion, StringComparison.Ordinal))
        {
            return ServiceResult<MyProductDraftResult>.Failure(new[]
            {
                new ValidationError("concurrency", "Row version mismatch")
            });
        }

        var finalPrice = _priceCalculator.ComputeFinalPrice(
            publication.CatalogPriceCentsSnapshot,
            request.PricingMode,
            request.MarkupPercent,
            request.FixedPriceCents);

        if (!finalPrice.Succeeded)
        {
            return ServiceResult<MyProductDraftResult>.Failure(finalPrice.Errors);
        }

        publication.PricingMode = request.PricingMode;
        publication.MarkupPercent = request.MarkupPercent;
        publication.FixedPriceCents = request.FixedPriceCents;
        publication.FinalPriceCentsSnapshot = finalPrice.Data;
        publication.UpdatedByUserId = userId;
        publication.UpdatedAt = DateTimeOffset.UtcNow;

        _dbContext.AuditEvents.Add(new AuditEvent
        {
            TenantId = tenantId,
            ActorType = "TenantUser",
            ActorId = userId,
            Action = "MyProducts.Update",
            Entity = nameof(Publication),
            EntityId = publication.Id,
            RequestId = Guid.NewGuid(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                publication.ProductSku,
                publication.PricingMode,
                publication.MarkupPercent,
                publication.FixedPriceCents
            })
        });

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult<MyProductDraftResult>.Failure(new[]
            {
                new ValidationError("concurrency", "Draft was changed by another request")
            });
        }

        var updated = await GetMyProductDraftByIdAsync(draftId, tenantId, clientId, cancellationToken);
        if (updated == null)
        {
            return ServiceResult<MyProductDraftResult>.Failure(new[]
            {
                new ValidationError("draftId", "Draft not found")
            });
        }

        return ServiceResult<MyProductDraftResult>.Success(updated);
    }

    public async Task<bool> RemoveMyProductDraftAsync(
        Guid draftId,
        string tenantId,
        Guid clientId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var publication = await _dbContext.Publications
            .FirstOrDefaultAsync(
                item => item.Id == draftId
                        && item.TenantId == tenantId
                        && item.ClientId == clientId
                        && item.Status == PublicationStatus.Draft,
                cancellationToken);

        if (publication == null)
        {
            return false;
        }

        _dbContext.Publications.Remove(publication);
        _dbContext.AuditEvents.Add(new AuditEvent
        {
            TenantId = tenantId,
            ActorType = "TenantUser",
            ActorId = userId,
            Action = "MyProducts.Delete",
            Entity = nameof(Publication),
            EntityId = publication.Id,
            RequestId = Guid.NewGuid(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                publication.ProductSku,
                publication.Status
            })
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<ServiceResult<AddMyProductOperationResult>> AddCoreAsync(
        string tenantId,
        Guid clientId,
        Guid userId,
        string sku,
        PricingMode pricingMode,
        decimal? markupPercent,
        long? fixedPriceCents,
        CancellationToken cancellationToken)
    {
        var isAllowed = await _catalogAuthorizationService.IsSkuAllowedAsync(tenantId, clientId, sku, cancellationToken);
        if (!isAllowed)
        {
            return ServiceResult<AddMyProductOperationResult>.Failure(new[]
            {
                new ValidationError("productSku", "SKU is not available for this client")
            });
        }

        var existingDraftProjection = await BuildDraftProjectionQuery(tenantId, clientId)
            .FirstOrDefaultAsync(item => item.ProductSku == sku, cancellationToken);

        if (existingDraftProjection != null)
        {
            var imagesBySku = await LoadImagesBySkuAsync(new[] { existingDraftProjection.ProductSku }, cancellationToken);
            var resolvedStockBySku = await LoadResolvedVariantStocksBySkuAsync(new[] { existingDraftProjection.ProductSku }, cancellationToken);
            return ServiceResult<AddMyProductOperationResult>.Success(new AddMyProductOperationResult
            {
                Created = false,
                Draft = MapDraftProjection(
                    existingDraftProjection,
                    imagesBySku.TryGetValue(existingDraftProjection.ProductSku, out var images)
                        ? images
                        : Array.Empty<MyProductImageResult>(),
                    resolvedStockBySku.TryGetValue(existingDraftProjection.ProductSku, out var resolvedStock) ? resolvedStock : null)
            });
        }

        var product = await _dbContext.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Sku == sku && item.IsActive, cancellationToken);

        if (product == null)
        {
            return ServiceResult<AddMyProductOperationResult>.Failure(new[]
            {
                new ValidationError("productSku", "Product not found")
            });
        }

        var finalPrice = _priceCalculator.ComputeFinalPrice(product.CatalogPriceCents, pricingMode, markupPercent, fixedPriceCents);
        if (!finalPrice.Succeeded)
        {
            return ServiceResult<AddMyProductOperationResult>.Failure(finalPrice.Errors);
        }

        var publication = new Publication
        {
            TenantId = tenantId,
            ClientId = clientId,
            ProductSku = sku,
            Status = PublicationStatus.Draft,
            PricingMode = pricingMode,
            MarkupPercent = markupPercent,
            FixedPriceCents = fixedPriceCents,
            CostPriceCentsSnapshot = product.CostPriceCents,
            CatalogPriceCentsSnapshot = product.CatalogPriceCents,
            FinalPriceCentsSnapshot = finalPrice.Data,
            PriceSnapshotTakenAt = DateTimeOffset.UtcNow,
            CreatedByUserId = userId,
            UpdatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.Publications.Add(publication);
        _dbContext.AuditEvents.Add(new AuditEvent
        {
            TenantId = tenantId,
            ActorType = "TenantUser",
            ActorId = userId,
            Action = "MyProducts.Add",
            Entity = nameof(Publication),
            EntityId = publication.Id,
            RequestId = Guid.NewGuid(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                publication.ProductSku,
                publication.PricingMode,
                publication.MarkupPercent,
                publication.FixedPriceCents
            })
        });

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            var duplicateDraftProjection = await BuildDraftProjectionQuery(tenantId, clientId)
                .FirstOrDefaultAsync(item => item.ProductSku == sku, cancellationToken);
            if (duplicateDraftProjection != null)
            {
                var imagesBySku = await LoadImagesBySkuAsync(new[] { duplicateDraftProjection.ProductSku }, cancellationToken);
                var resolvedStockBySku = await LoadResolvedVariantStocksBySkuAsync(new[] { duplicateDraftProjection.ProductSku }, cancellationToken);
                return ServiceResult<AddMyProductOperationResult>.Success(new AddMyProductOperationResult
                {
                    Created = false,
                    Draft = MapDraftProjection(
                        duplicateDraftProjection,
                        imagesBySku.TryGetValue(duplicateDraftProjection.ProductSku, out var images)
                            ? images
                            : Array.Empty<MyProductImageResult>(),
                        resolvedStockBySku.TryGetValue(duplicateDraftProjection.ProductSku, out var resolvedStock) ? resolvedStock : null)
                });
            }

            throw;
        }

        var createdDraft = await GetMyProductDraftByIdAsync(publication.Id, tenantId, clientId, cancellationToken);
        if (createdDraft == null)
        {
            return ServiceResult<AddMyProductOperationResult>.Failure(new[]
            {
                new ValidationError("draft", "Failed to load created draft")
            });
        }

        return ServiceResult<AddMyProductOperationResult>.Success(new AddMyProductOperationResult
        {
            Created = true,
            Draft = createdDraft
        });
    }

    private IQueryable<DraftProjection> BuildDraftProjectionQuery(string tenantId, Guid clientId)
    {
        return
            from draft in _dbContext.Publications.AsNoTracking()
            join product in _dbContext.Products.AsNoTracking() on draft.ProductSku equals product.Sku into products
            from product in products.DefaultIfEmpty()
            where draft.TenantId == tenantId
                  && draft.ClientId == clientId
                  && draft.Status == PublicationStatus.Draft
            select new DraftProjection
            {
                Id = draft.Id,
                ProductSku = draft.ProductSku,
                ProductName = product != null ? product.Name : draft.ProductSku,
                ThumbnailUrl = product != null ? product.ThumbnailUrl : null,
                Status = draft.Status,
                PricingMode = draft.PricingMode,
                MarkupPercent = draft.MarkupPercent,
                FixedPriceCents = draft.FixedPriceCents,
                CatalogPriceCentsSnapshot = draft.CatalogPriceCentsSnapshot,
                FinalPriceCentsSnapshot = draft.FinalPriceCentsSnapshot,
                PriceSnapshotTakenAt = draft.PriceSnapshotTakenAt,
                Description = product != null ? product.Description : null,
                Gtin = product != null ? product.Ean : null,
                Ncm = product != null ? product.Ncm : null,
                Origin = "Nacional",
                PurchaseCostCents = _dbContext.ProductVariants.AsNoTracking()
                    .Where(variant => variant.VariantSku == draft.ProductSku || variant.BaseSku == draft.ProductSku)
                    .OrderByDescending(variant => variant.VariantSku == draft.ProductSku)
                    .ThenBy(variant => variant.VariantSku)
                    .Select(variant => (long?)variant.CostPriceCents)
                    .FirstOrDefault() ?? (product != null ? product.CostPriceCents : 0),
                CatalogPriceCents = _dbContext.ProductVariants.AsNoTracking()
                    .Where(variant => variant.VariantSku == draft.ProductSku || variant.BaseSku == draft.ProductSku)
                    .OrderByDescending(variant => variant.VariantSku == draft.ProductSku)
                    .ThenBy(variant => variant.VariantSku)
                    .Select(variant => (long?)variant.CatalogPriceCents)
                    .FirstOrDefault() ?? (product != null ? product.CatalogPriceCents : 0),
                RowVersion = EF.Property<uint>(draft, "xmin"),
                HasProductVariant = _dbContext.ProductVariants.AsNoTracking()
                    .Any(variant => variant.VariantSku == draft.ProductSku || variant.BaseSku == draft.ProductSku),
                CreatedAt = draft.CreatedAt,
                UpdatedAt = draft.UpdatedAt
            };
    }

    private async Task<Dictionary<string, IReadOnlyList<MyProductImageResult>>> LoadImagesBySkuAsync(
        IReadOnlyCollection<string> productSkus,
        CancellationToken cancellationToken)
    {
        var normalizedSkus = productSkus
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(Sku.Normalize)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalizedSkus.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<MyProductImageResult>>(StringComparer.Ordinal);
        }

        var images = await _dbContext.ProductImages.AsNoTracking()
            .Where(item => normalizedSkus.Contains(item.ProductSku))
            .OrderBy(item => item.ProductSku)
            .ThenByDescending(item => item.IsPrimary)
            .ThenBy(item => item.SortOrder)
            .ThenBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.ProductSku,
                item.Url
            })
            .ToListAsync(cancellationToken);

        return images
            .GroupBy(item => item.ProductSku, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<MyProductImageResult>)group
                    .Where(item => !string.IsNullOrWhiteSpace(item.Url))
                    .Select((item, index) => new MyProductImageResult
                    {
                        Url = item.Url.Trim(),
                        Position = index + 1
                    })
                    .Take(10)
                    .ToList(),
                StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, ResolvedVariantStock>> LoadResolvedVariantStocksBySkuAsync(
        IReadOnlyCollection<string> productSkus,
        CancellationToken cancellationToken)
    {
        var normalizedSkus = productSkus
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(Sku.Normalize)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalizedSkus.Count == 0)
        {
            return new Dictionary<string, ResolvedVariantStock>(StringComparer.Ordinal);
        }

        var variants = await _dbContext.ProductVariants
            .AsNoTracking()
            .Where(item => normalizedSkus.Contains(item.VariantSku) || normalizedSkus.Contains(item.BaseSku))
            .ToListAsync(cancellationToken);

        var response = new Dictionary<string, ResolvedVariantStock>(StringComparer.Ordinal);
        foreach (var sku in normalizedSkus)
        {
            var matchingVariants = variants
                .Where(item => string.Equals(item.VariantSku, sku, StringComparison.Ordinal) || string.Equals(item.BaseSku, sku, StringComparison.Ordinal))
                .ToList();
            response[sku] = VariantStockResolver.Resolve(sku, matchingVariants);
        }

        return response;
    }

    private async Task<List<string>?> ResolveExactSkuFilterAsync(
        string? variantSku,
        IReadOnlyCollection<string>? variantSkus,
        CancellationToken cancellationToken)
    {
        var requested = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(variantSku))
        {
            requested.Add(Sku.Normalize(variantSku));
        }

        if (variantSkus != null)
        {
            foreach (var item in variantSkus)
            {
                if (!string.IsNullOrWhiteSpace(item))
                {
                    requested.Add(Sku.Normalize(item));
                }
            }
        }

        if (requested.Count == 0)
        {
            return null;
        }

        var baseSkus = await _dbContext.ProductVariants.AsNoTracking()
            .Where(item => requested.Contains(item.VariantSku))
            .Select(item => item.BaseSku)
            .Distinct()
            .ToListAsync(cancellationToken);

        var filter = requested.ToHashSet(StringComparer.Ordinal);
        foreach (var baseSku in baseSkus)
        {
            filter.Add(baseSku);
        }

        return filter.ToList();
    }

    private async Task<Dictionary<string, MlBadgeSummary>> LoadMlBadgeSummaryAsync(
        string tenantId,
        Guid clientId,
        IReadOnlyCollection<string> baseSkus,
        CancellationToken cancellationToken)
    {
        if (baseSkus.Count == 0)
        {
            return new Dictionary<string, MlBadgeSummary>(StringComparer.Ordinal);
        }

        var normalizedBaseSkus = baseSkus
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(Sku.Normalize)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalizedBaseSkus.Count == 0)
        {
            return new Dictionary<string, MlBadgeSummary>(StringComparer.Ordinal);
        }

        var draftRows = await _dbContext.ListingDrafts
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == MarketplaceProvider.MercadoLivre
                           && normalizedBaseSkus.Contains(item.BaseProductSku))
            .GroupBy(item => item.BaseProductSku)
            .Select(group => new
            {
                BaseSku = group.Key,
                Published = group.Count(item => item.Status == ListingDraftStatus.Published),
                Draft = group.Count(item =>
                    item.Status == ListingDraftStatus.Draft ||
                    item.Status == ListingDraftStatus.Valid ||
                    item.Status == ListingDraftStatus.Publishing),
                Error = group.Count(item => item.Status == ListingDraftStatus.Error)
            })
            .ToListAsync(cancellationToken);

        var mapFallbackRows = await (
                from map in _dbContext.TenantMarketplaceListingMaps.AsNoTracking()
                join variant in _dbContext.ProductVariants.AsNoTracking()
                    on map.SabrVariantSku equals variant.VariantSku
                where map.TenantId == tenantId
                      && map.ClientId == clientId
                      && map.Provider == MarketplaceProvider.MercadoLivre
                      && normalizedBaseSkus.Contains(variant.BaseSku)
                group map by variant.BaseSku
                into groupByBase
                select new
                {
                    BaseSku = groupByBase.Key,
                    Published = groupByBase.Count()
                })
            .ToListAsync(cancellationToken);

        var response = new Dictionary<string, MlBadgeSummary>(StringComparer.Ordinal);
        foreach (var row in draftRows)
        {
            response[row.BaseSku] = BuildBadge(row.Published, row.Draft, row.Error);
        }

        foreach (var fallback in mapFallbackRows)
        {
            if (response.ContainsKey(fallback.BaseSku))
            {
                continue;
            }

            response[fallback.BaseSku] = BuildBadge(fallback.Published, 0, 0);
        }

        return response;
    }

    private static MlBadgeSummary BuildBadge(int published, int draft, int error)
    {
        var status = error > 0
            ? "Error"
            : draft > 0
                ? "Draft"
                : published > 0
                    ? "Published"
                    : "None";

        return new MlBadgeSummary
        {
            PublishedCount = published,
            DraftCount = draft,
            ErrorCount = error,
            OverallStatus = status
        };
    }

    private MyProductDraftResult MapDraftProjection(
        DraftProjection projection,
        IReadOnlyList<MyProductImageResult> images,
        ResolvedVariantStock? resolvedStock = null)
    {
        return new MyProductDraftResult
        {
            Id = projection.Id,
            ProductSku = projection.ProductSku,
            ProductName = projection.ProductName,
            ThumbnailUrl = projection.ThumbnailUrl,
            Status = projection.Status,
            PricingMode = projection.PricingMode,
            MarkupPercent = projection.MarkupPercent,
            FixedPriceCents = projection.FixedPriceCents,
            CatalogPriceCentsSnapshot = projection.CatalogPriceCentsSnapshot,
            FinalPriceCentsSnapshot = projection.FinalPriceCentsSnapshot,
            PriceSnapshotTakenAt = projection.PriceSnapshotTakenAt,
            Description = projection.Description,
            Images = images.ToList(),
            Gtin = projection.Gtin,
            Ncm = projection.Ncm,
            Origin = projection.Origin,
            PurchaseCost = ListingDraftHelpers.ToDecimal(Math.Max(0, projection.PurchaseCostCents)),
            CatalogPrice = ListingDraftHelpers.ToDecimal(Math.Max(0, projection.CatalogPriceCents)),
            RowVersion = ResolveRowVersion(projection.RowVersion, projection.UpdatedAt),
            HasProductVariant = projection.HasProductVariant,
            VariantStatus = projection.HasProductVariant ? "Ready" : "Missing",
            ResolvedVariantSku = resolvedStock?.ResolvedVariantSku,
            AvailableStock = resolvedStock?.AvailableStock,
            StockSource = resolvedStock?.StockSource.ToString() ?? "FallbackZero",
            CreatedAt = projection.CreatedAt,
            UpdatedAt = projection.UpdatedAt
        };
    }

    private async Task<(bool Allowed, IdempotencyKey? Entity, AddMyProductOperationResult? CachedResult, List<ValidationError> Errors)> TryAcquireIdempotencyAsync(
        string tenantId,
        string key,
        string hash,
        CancellationToken cancellationToken)
    {
        var errors = new List<ValidationError>();
        var existing = await _dbContext.IdempotencyKeys
            .FirstOrDefaultAsync(item => item.TenantId == tenantId && item.Scope == IdempotencyScope && item.Key == key, cancellationToken);

        if (existing != null)
        {
            if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
            {
                errors.Add(new ValidationError("idempotency", "Idempotency key payload mismatch"));
                return (false, null, null, errors);
            }

            if (existing.Status == IdempotencyStatus.Completed && !string.IsNullOrWhiteSpace(existing.ResponseJson))
            {
                var cached = JsonSerializer.Deserialize<AddMyProductOperationResult>(existing.ResponseJson);
                if (cached != null)
                {
                    return (true, existing, cached, errors);
                }
            }

            if (existing.Status == IdempotencyStatus.Started)
            {
                errors.Add(new ValidationError("idempotency", "Idempotency key in progress"));
                return (false, null, null, errors);
            }

            if (existing.Status == IdempotencyStatus.Failed)
            {
                errors.Add(new ValidationError("idempotency", "Idempotency key cannot be reused after failure"));
                return (false, null, null, errors);
            }

            return (true, existing, null, errors);
        }

        var entity = new IdempotencyKey
        {
            TenantId = tenantId,
            Scope = IdempotencyScope,
            Key = key,
            RequestHash = hash,
            Status = IdempotencyStatus.Started,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.IdempotencyKeys.Add(entity);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return (true, entity, null, errors);
        }
        catch (DbUpdateException)
        {
            errors.Add(new ValidationError("idempotency", "Idempotency key conflict"));
            return (false, null, null, errors);
        }
    }

    private static List<ValidationError> ValidateAddRequest(AddMyProductRequest request, string tenantId, Guid clientId, Guid userId)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            errors.Add(new ValidationError("tenantId", "TenantId is required"));
        }

        if (clientId == Guid.Empty)
        {
            errors.Add(new ValidationError("clientId", "ClientId is required"));
        }

        if (userId == Guid.Empty)
        {
            errors.Add(new ValidationError("userId", "UserId is required"));
        }

        if (string.IsNullOrWhiteSpace(request.ProductSku))
        {
            errors.Add(new ValidationError("productSku", "Product SKU is required"));
        }
        else if (!Sku.TryParse(request.ProductSku, out _))
        {
            errors.Add(new ValidationError("productSku", "Product SKU format is invalid"));
        }

        return errors;
    }

    private static List<ValidationError> ValidateUpdateRequest(UpdateMyProductDraftRequest request, string tenantId, Guid clientId, Guid userId)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            errors.Add(new ValidationError("tenantId", "TenantId is required"));
        }

        if (clientId == Guid.Empty)
        {
            errors.Add(new ValidationError("clientId", "ClientId is required"));
        }

        if (userId == Guid.Empty)
        {
            errors.Add(new ValidationError("userId", "UserId is required"));
        }

        return errors;
    }

    private static string HashAddRequest(
        string tenantId,
        Guid clientId,
        string sku,
        PricingMode pricingMode,
        decimal? markupPercent,
        long? fixedPriceCents)
    {
        var json = JsonSerializer.Serialize(new
        {
            tenantId,
            clientId,
            sku,
            pricingMode,
            markupPercent,
            fixedPriceCents
        });

        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private string GetRowVersion(DbContext dbContext, Publication publication)
    {
        // `xmin` is exposed as uint concurrency token by Npgsql and converted to a stable numeric string.
        var xmin = dbContext.Entry(publication).Property<uint>("xmin").CurrentValue;
        return ResolveRowVersion(xmin, publication.UpdatedAt);
    }

    private string ResolveRowVersion(uint xmin, DateTimeOffset updatedAt)
    {
        if (_isNpgsqlProvider)
        {
            return xmin.ToString(CultureInfo.InvariantCulture);
        }

        // In non-Npgsql providers (e.g., test InMemory), xmin is not managed by the provider.
        // Fallback is test-only and preserves a numeric optimistic version token.
        return updatedAt.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture);
    }

    private static string? ResolveExpectedRowVersion(string? ifMatch, string? rowVersion)
    {
        if (!string.IsNullOrWhiteSpace(ifMatch))
        {
            var raw = ifMatch.Trim();
            if (raw.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            {
                raw = raw.Substring(2).Trim();
            }

            raw = raw.Trim('"');
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return raw;
            }
        }

        return string.IsNullOrWhiteSpace(rowVersion) ? null : rowVersion.Trim();
    }

    private sealed class DraftProjection
    {
        public Guid Id { get; set; }
        public string ProductSku { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string? ThumbnailUrl { get; set; }
        public PublicationStatus Status { get; set; }
        public PricingMode PricingMode { get; set; }
        public decimal? MarkupPercent { get; set; }
        public long? FixedPriceCents { get; set; }
        public long CatalogPriceCentsSnapshot { get; set; }
        public long FinalPriceCentsSnapshot { get; set; }
        public DateTimeOffset PriceSnapshotTakenAt { get; set; }
        public string? Description { get; set; }
        public string? Gtin { get; set; }
        public string? Ncm { get; set; }
        public string? Origin { get; set; }
        public long PurchaseCostCents { get; set; }
        public long CatalogPriceCents { get; set; }
        public uint RowVersion { get; set; }
        public bool HasProductVariant { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class MlBadgeSummary
    {
        public int PublishedCount { get; set; }
        public int DraftCount { get; set; }
        public int ErrorCount { get; set; }
        public string OverallStatus { get; set; } = "None";
    }
}
