using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Validation;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;
using Sabr.Domain.ValueObjects;

namespace Sabr.Application.Services;

public sealed class ProductAdminService
{
    private static readonly Regex NcmRegex = new("^[0-9]{8}$", RegexOptions.Compiled);
    private static readonly Regex EanRegex = new("^(?:[0-9]{8}|[0-9]{12}|[0-9]{13}|[0-9]{14})$", RegexOptions.Compiled);
    private static readonly Regex AnatelRegex = new("^[0-9\\-/]{6,32}$", RegexOptions.Compiled);
    private static readonly Regex CategorySlugRegex = new("^[a-z0-9][a-z0-9_/-]{0,119}$", RegexOptions.Compiled);
    public const string UncategorizedSlug = "uncategorized";
    private readonly IAppDbContext _dbContext;
    private readonly ILogger<ProductAdminService> _logger;

    public ProductAdminService(
        IAppDbContext dbContext,
        ILogger<ProductAdminService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<ServiceResult<PagedResult<AdminProductResult>>> ListAsync(
        int skip,
        int limit,
        string? search,
        bool? isActive,
        CancellationToken cancellationToken = default)
    {
        var errors = PaginationGuard.ValidateOrError(skip, limit);
        if (errors.Count > 0)
        {
            return ServiceResult<PagedResult<AdminProductResult>>.Failure(errors);
        }

        var query = _dbContext.Products.AsNoTracking().AsQueryable();
        if (isActive.HasValue)
        {
            query = query.Where(product => product.IsActive == isActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            query = query.Where(product =>
                product.Sku.ToUpper().Contains(term) ||
                product.Name.ToUpper().Contains(term) ||
                product.Brand.ToUpper().Contains(term) ||
                (product.CategoryId != null && product.CategoryId.ToUpper().Contains(term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(product => product.UpdatedAt)
            .ThenBy(product => product.Sku)
            .Skip(skip)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return ServiceResult<PagedResult<AdminProductResult>>.Success(new PagedResult<AdminProductResult>
        {
            Items = items.Select(MapToAdminResultWithoutImages).ToList(),
            Total = total,
            Skip = skip,
            Limit = limit
        });
    }

    public async Task<ServiceResult<AdminProductResult>> GetBySkuAsync(
        string sku,
        CancellationToken cancellationToken = default)
    {
        if (!Sku.TryParse(sku, out var parsed))
        {
            return ServiceResult<AdminProductResult>.Failure(new[]
            {
                new ValidationError("sku", "SKU format is invalid")
            });
        }

        var product = await _dbContext.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Sku == parsed.Value, cancellationToken);

        if (product == null)
        {
            return ServiceResult<AdminProductResult>.Failure(new[]
            {
                new ValidationError("sku", "Product not found")
            });
        }

        var images = await _dbContext.ProductImages
            .AsNoTracking()
            .Where(item => item.ProductSku == product.Sku)
            .OrderByDescending(item => item.IsPrimary)
            .ThenBy(item => item.SortOrder)
            .ThenBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);

        return ServiceResult<AdminProductResult>.Success(MapToAdminResult(product, images));
    }

    public async Task<ServiceResult<ProductPricingUpdateResult>> UpsertProductAsync(
        AdminProductUpsertRequest request,
        Guid actorUserId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateUpsertRequest(request);
        if (actorUserId == Guid.Empty)
        {
            errors.Add(new ValidationError("actor", "Actor user is required"));
        }

        if (errors.Count > 0)
        {
            return ServiceResult<ProductPricingUpdateResult>.Failure(errors);
        }

        var normalizedSku = Sku.Normalize(request.Sku);
        var categoryResolution = await ResolveCategoryForUpsertAsync(request.CategoryId, cancellationToken);
        if (!categoryResolution.Succeeded || string.IsNullOrWhiteSpace(categoryResolution.Data))
        {
            return ServiceResult<ProductPricingUpdateResult>.Failure(categoryResolution.Errors);
        }
        var resolvedCategorySlug = categoryResolution.Data;

        var efDbContext = (DbContext)_dbContext;
        await using var transaction = await BeginTransactionIfSupportedAsync(efDbContext, cancellationToken);

        var product = await _dbContext.Products.FirstOrDefaultAsync(p => p.Sku == normalizedSku, cancellationToken);
        if (product == null)
        {
            product = new Product
            {
                Sku = normalizedSku,
                Name = request.Name.Trim(),
                Brand = request.Brand.Trim(),
                Ncm = NormalizeOptionalText(request.Ncm),
                Ean = NormalizeOptionalText(request.Ean),
                Description = NormalizeOptionalText(request.Description),
                CategoryId = resolvedCategorySlug,
                ThumbnailUrl = NormalizeOptionalText(request.ThumbnailUrl),
                WidthCm = request.WidthCm,
                HeightCm = request.HeightCm,
                LengthCm = request.LengthCm,
                WeightKg = request.WeightKg,
                RequiresAnatel = request.RequiresAnatel,
                AnatelHomologationNumber = NormalizeOptionalText(request.AnatelHomologationNumber),
                AnatelDocumentId = request.AnatelDocumentId,
                CostPriceCents = request.CostPriceCents,
                CatalogPriceCents = request.CatalogPriceCents,
                IsActive = request.IsActive,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            _dbContext.Products.Add(product);
            AddAuditEvent("AdminProducts.Create", actorUserId, tenantId, product.Sku, new
            {
                product.Sku,
                product.IsActive
            });

            await SyncListingDraftsFromAdminProductAsync(product, "AdminProducts.Create", cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return ServiceResult<ProductPricingUpdateResult>.Success(MapToPricingResult(product));
        }

        var oldCost = product.CostPriceCents;
        var oldCatalog = product.CatalogPriceCents;

        ApplyUpsertValues(product, request, resolvedCategorySlug);
        product.UpdatedAt = DateTimeOffset.UtcNow;

        if (oldCost != product.CostPriceCents || oldCatalog != product.CatalogPriceCents)
        {
            _dbContext.ProductPriceHistories.Add(new ProductPriceHistory
            {
                TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
                ProductSku = product.Sku,
                OldCostPriceCents = oldCost,
                NewCostPriceCents = product.CostPriceCents,
                OldCatalogPriceCents = oldCatalog,
                NewCatalogPriceCents = product.CatalogPriceCents,
                ChangedByUserId = actorUserId,
                ChangedAt = DateTimeOffset.UtcNow,
                Reason = "Admin product upsert"
            });
        }

        AddAuditEvent("AdminProducts.Update", actorUserId, tenantId, product.Sku, new
        {
            product.Sku,
            product.IsActive
        });

        await SyncListingDraftsFromAdminProductAsync(product, "AdminProducts.Update", cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return ServiceResult<ProductPricingUpdateResult>.Success(MapToPricingResult(product));
    }

    public async Task<ServiceResult<AdminProductResult>> UpdateBySkuAsync(
        string sku,
        AdminProductUpsertRequest request,
        Guid actorUserId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        if (!Sku.TryParse(sku, out var parsedSku))
        {
            return ServiceResult<AdminProductResult>.Failure(new[]
            {
                new ValidationError("sku", "SKU format is invalid")
            });
        }

        var adjusted = new AdminProductUpsertRequest
        {
            Sku = parsedSku.Value,
            Name = request.Name,
            Brand = request.Brand,
            Ncm = request.Ncm,
            Ean = request.Ean,
            Description = request.Description,
            CategoryId = request.CategoryId,
            ThumbnailUrl = request.ThumbnailUrl,
            WidthCm = request.WidthCm,
            HeightCm = request.HeightCm,
            LengthCm = request.LengthCm,
            WeightKg = request.WeightKg,
            RequiresAnatel = request.RequiresAnatel,
            AnatelHomologationNumber = request.AnatelHomologationNumber,
            AnatelDocumentId = request.AnatelDocumentId,
            TenantSlug = request.TenantSlug,
            CostPriceCents = request.CostPriceCents,
            CatalogPriceCents = request.CatalogPriceCents,
            IsActive = request.IsActive
        };

        var upsertResult = await UpsertProductAsync(adjusted, actorUserId, tenantId, cancellationToken);
        if (!upsertResult.Succeeded)
        {
            return ServiceResult<AdminProductResult>.Failure(upsertResult.Errors);
        }

        return await GetBySkuAsync(parsedSku.Value, cancellationToken);
    }

    public async Task<ServiceResult<AdminProductResult>> PatchBySkuAsync(
        string sku,
        AdminProductUpdateRequest request,
        Guid actorUserId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        if (!Sku.TryParse(sku, out var parsedSku))
        {
            return ServiceResult<AdminProductResult>.Failure(new[]
            {
                new ValidationError("sku", "SKU format is invalid")
            });
        }

        if (actorUserId == Guid.Empty)
        {
            return ServiceResult<AdminProductResult>.Failure(new[]
            {
                new ValidationError("actor", "Actor user is required")
            });
        }

        var product = await _dbContext.Products.FirstOrDefaultAsync(item => item.Sku == parsedSku.Value, cancellationToken);
        if (product == null)
        {
            return ServiceResult<AdminProductResult>.Failure(new[]
            {
                new ValidationError("sku", "Product not found")
            });
        }

        var candidate = BuildCandidate(product, request);
        var categoryResolution = await ResolveCategoryForPatchAsync(
            product.CategoryId,
            request.CategoryId,
            request.CategoryIdProvided,
            cancellationToken);
        if (!categoryResolution.Succeeded || string.IsNullOrWhiteSpace(categoryResolution.Data))
        {
            return ServiceResult<AdminProductResult>.Failure(categoryResolution.Errors);
        }

        candidate.CategoryId = categoryResolution.Data;
        var errors = ValidateProductFields(candidate);
        if (errors.Count > 0)
        {
            return ServiceResult<AdminProductResult>.Failure(errors);
        }

        var oldCost = product.CostPriceCents;
        var oldCatalog = product.CatalogPriceCents;

        ApplyCandidate(product, candidate);
        product.UpdatedAt = DateTimeOffset.UtcNow;

        if (oldCost != product.CostPriceCents || oldCatalog != product.CatalogPriceCents)
        {
            _dbContext.ProductPriceHistories.Add(new ProductPriceHistory
            {
                TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
                ProductSku = product.Sku,
                OldCostPriceCents = oldCost,
                NewCostPriceCents = product.CostPriceCents,
                OldCatalogPriceCents = oldCatalog,
                NewCatalogPriceCents = product.CatalogPriceCents,
                ChangedByUserId = actorUserId,
                ChangedAt = DateTimeOffset.UtcNow,
                Reason = string.IsNullOrWhiteSpace(request.Reason) ? "Admin product update" : request.Reason.Trim()
            });
        }

        AddAuditEvent("AdminProducts.Update", actorUserId, tenantId, product.Sku, new
        {
            product.Sku,
            product.IsActive
        });

        await SyncListingDraftsFromAdminProductAsync(product, "AdminProducts.Update", cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await GetBySkuAsync(product.Sku, cancellationToken);
    }

    public async Task<ServiceResult<ProductPricingUpdateResult>> UpdatePricingAsync(
        string sku,
        AdminProductPricingUpdateRequest request,
        Guid actorUserId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(sku))
        {
            errors.Add(new ValidationError("sku", "SKU is required"));
        }
        else if (!Sku.TryParse(sku, out _))
        {
            errors.Add(new ValidationError("sku", "SKU format is invalid"));
        }

        if (request.CostPriceCents < 0)
        {
            errors.Add(new ValidationError("costPriceCents", "Cost price cannot be negative"));
        }

        if (request.CatalogPriceCents < 0)
        {
            errors.Add(new ValidationError("catalogPriceCents", "Catalog price cannot be negative"));
        }

        if (!string.IsNullOrWhiteSpace(request.Reason) && request.Reason.Length > 200)
        {
            errors.Add(new ValidationError("reason", "Reason cannot exceed 200 characters"));
        }

        if (actorUserId == Guid.Empty)
        {
            errors.Add(new ValidationError("actor", "Actor user is required"));
        }

        if (errors.Count > 0)
        {
            return ServiceResult<ProductPricingUpdateResult>.Failure(errors);
        }

        var efDbContext = (DbContext)_dbContext;
        await using var transaction = await BeginTransactionIfSupportedAsync(efDbContext, cancellationToken);

        var normalizedSku = Sku.Normalize(sku);
        var product = await _dbContext.Products.FirstOrDefaultAsync(p => p.Sku == normalizedSku, cancellationToken);
        if (product == null)
        {
            return ServiceResult<ProductPricingUpdateResult>.Failure(new[]
            {
                new ValidationError("sku", "Product not found")
            });
        }

        var oldCost = product.CostPriceCents;
        var oldCatalog = product.CatalogPriceCents;

        product.CostPriceCents = request.CostPriceCents;
        product.CatalogPriceCents = request.CatalogPriceCents;
        product.UpdatedAt = DateTimeOffset.UtcNow;

        if (oldCost != product.CostPriceCents || oldCatalog != product.CatalogPriceCents)
        {
            _dbContext.ProductPriceHistories.Add(new ProductPriceHistory
            {
                TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
                ProductSku = product.Sku,
                OldCostPriceCents = oldCost,
                NewCostPriceCents = product.CostPriceCents,
                OldCatalogPriceCents = oldCatalog,
                NewCatalogPriceCents = product.CatalogPriceCents,
                ChangedByUserId = actorUserId,
                ChangedAt = DateTimeOffset.UtcNow,
                Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim()
            });
        }

        AddAuditEvent("AdminProducts.UpdatePricing", actorUserId, tenantId, product.Sku, new
        {
            product.Sku,
            request.CostPriceCents,
            request.CatalogPriceCents
        });

        await SyncListingDraftsFromAdminProductAsync(product, "AdminProducts.UpdatePricing", cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return ServiceResult<ProductPricingUpdateResult>.Success(MapToPricingResult(product));
    }

    public async Task<ServiceResult<bool>> DeactivateAsync(
        string sku,
        Guid actorUserId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        if (!Sku.TryParse(sku, out var parsedSku))
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("sku", "SKU format is invalid")
            });
        }

        var product = await _dbContext.Products.FirstOrDefaultAsync(item => item.Sku == parsedSku.Value, cancellationToken);
        if (product == null || !product.IsActive)
        {
            return ServiceResult<bool>.Success(false);
        }

        product.IsActive = false;
        product.UpdatedAt = DateTimeOffset.UtcNow;
        AddAuditEvent("AdminProducts.Deactivate", actorUserId, tenantId, product.Sku, new
        {
            product.Sku
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<IReadOnlyCollection<Guid>>> GetLinkedCatalogIdsGlobalAsync(
        string sku,
        CancellationToken cancellationToken = default)
    {
        if (!Sku.TryParse(sku, out var parsedSku))
        {
            return ServiceResult<IReadOnlyCollection<Guid>>.Failure(new[]
            {
                new ValidationError("sku", "SKU format is invalid")
            });
        }

        var ids = await _dbContext.ProductCatalogs
            .AsNoTracking()
            .Where(item => item.ProductSku == parsedSku.Value)
            .Select(item => item.CatalogId)
            .Distinct()
            .OrderBy(item => item)
            .ToListAsync(cancellationToken);

        return ServiceResult<IReadOnlyCollection<Guid>>.Success(ids);
    }

    public async Task<ServiceResult<AdminProductResult>> ReplaceCatalogsGlobalAsync(
        string sku,
        ProductReplaceCatalogsRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (actorUserId == Guid.Empty)
        {
            return ServiceResult<AdminProductResult>.Failure(new[]
            {
                new ValidationError("actor", "Actor user is required")
            });
        }

        if (!Sku.TryParse(sku, out var parsedSku))
        {
            return ServiceResult<AdminProductResult>.Failure(new[]
            {
                new ValidationError("sku", "SKU format is invalid")
            });
        }

        var product = await _dbContext.Products.FirstOrDefaultAsync(item => item.Sku == parsedSku.Value, cancellationToken);
        if (product == null)
        {
            return ServiceResult<AdminProductResult>.Failure(new[]
            {
                new ValidationError("sku", "Product not found")
            });
        }

        request ??= new ProductReplaceCatalogsRequest();
        var desiredCatalogIds = (request.CatalogIds ?? new List<Guid>())
            .Where(item => item != Guid.Empty)
            .Distinct()
            .ToList();

        if (product.IsActive && desiredCatalogIds.Count == 0)
        {
            return ServiceResult<AdminProductResult>.Failure(new[]
            {
                new ValidationError("catalogLinks", "Active product must be linked to at least one catalog")
            });
        }

        var validCatalogIds = await _dbContext.Catalogs
            .AsNoTracking()
            .Where(item => desiredCatalogIds.Contains(item.Id) && item.IsActive)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);

        var invalidCatalogIds = desiredCatalogIds.Except(validCatalogIds).ToList();
        if (invalidCatalogIds.Count > 0)
        {
            var invalidErrors = invalidCatalogIds
                .Select(item => new ValidationError("invalidCatalogIds", item.ToString()))
                .ToList();
            invalidErrors.Add(new ValidationError("catalogIds", "One or more catalog ids are invalid or inactive"));
            return ServiceResult<AdminProductResult>.Failure(invalidErrors);
        }

        var efDbContext = (DbContext)_dbContext;
        await using var transaction = await BeginTransactionIfSupportedAsync(efDbContext, cancellationToken);

        var currentLinks = await _dbContext.ProductCatalogs
            .Where(item => item.ProductSku == parsedSku.Value)
            .ToListAsync(cancellationToken);

        var currentSet = currentLinks.Select(item => item.CatalogId).ToHashSet();
        var desiredSet = desiredCatalogIds.ToHashSet();

        var toRemove = currentLinks.Where(item => !desiredSet.Contains(item.CatalogId)).ToList();
        var toAdd = desiredCatalogIds.Where(item => !currentSet.Contains(item)).ToList();

        if (toRemove.Count > 0)
            _dbContext.ProductCatalogs.RemoveRange(toRemove);

        if (toAdd.Count > 0)
        {
            _dbContext.ProductCatalogs.AddRange(toAdd.Select(item => new ProductCatalog
            {
                CatalogId = item,
                ProductSku = parsedSku.Value,
                CreatedAt = DateTimeOffset.UtcNow
            }));
        }

        AddAuditEvent("AdminProducts.ReplaceCatalogs", actorUserId, string.Empty, parsedSku.Value, new
        {
            Added = toAdd.Count,
            Removed = toRemove.Count
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        if (transaction != null)
            await transaction.CommitAsync(cancellationToken);

        return await GetBySkuAsync(parsedSku.Value, cancellationToken);
    }

    public async Task<ServiceResult<IReadOnlyCollection<Guid>>> GetLinkedCatalogIdsAsync(
        string tenantSlug,
        string sku,
        CancellationToken cancellationToken = default)
    {
        var tenantResult = await ResolveTenantAsync(tenantSlug, cancellationToken);
        if (!tenantResult.Succeeded || tenantResult.Data == null)
        {
            return ServiceResult<IReadOnlyCollection<Guid>>.Failure(tenantResult.Errors);
        }

        if (!Sku.TryParse(sku, out var parsedSku))
        {
            return ServiceResult<IReadOnlyCollection<Guid>>.Failure(new[]
            {
                new ValidationError("sku", "SKU format is invalid")
            });
        }

        var ids = await _dbContext.ProductCatalogs
            .AsNoTracking()
            .Where(item => item.ProductSku == parsedSku.Value)
            .Select(item => item.CatalogId)
            .Distinct()
            .OrderBy(item => item)
            .ToListAsync(cancellationToken);

        return ServiceResult<IReadOnlyCollection<Guid>>.Success(ids);
    }

    public async Task<ServiceResult<AdminProductResult>> ReplaceCatalogsAsync(
        string tenantSlug,
        string sku,
        ProductReplaceCatalogsRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var tenantResult = await ResolveTenantAsync(tenantSlug, cancellationToken);
        if (!tenantResult.Succeeded || tenantResult.Data == null)
        {
            return ServiceResult<AdminProductResult>.Failure(tenantResult.Errors);
        }

        if (actorUserId == Guid.Empty)
        {
            return ServiceResult<AdminProductResult>.Failure(new[]
            {
                new ValidationError("actor", "Actor user is required")
            });
        }

        if (!Sku.TryParse(sku, out var parsedSku))
        {
            return ServiceResult<AdminProductResult>.Failure(new[]
            {
                new ValidationError("sku", "SKU format is invalid")
            });
        }

        var product = await _dbContext.Products.FirstOrDefaultAsync(item => item.Sku == parsedSku.Value, cancellationToken);
        if (product == null)
        {
            return ServiceResult<AdminProductResult>.Failure(new[]
            {
                new ValidationError("sku", "Product not found")
            });
        }

        var tenant = tenantResult.Data;
        request ??= new ProductReplaceCatalogsRequest();
        var desiredCatalogIds = (request.CatalogIds ?? new List<Guid>())
            .Where(item => item != Guid.Empty)
            .Distinct()
            .ToList();

        if (product.IsActive && desiredCatalogIds.Count == 0)
        {
            return ServiceResult<AdminProductResult>.Failure(new[]
            {
                new ValidationError("catalogLinks", "Active product must be linked to at least one catalog")
            });
        }

        var validCatalogIds = await _dbContext.Catalogs
            .AsNoTracking()
            .Where(item => desiredCatalogIds.Contains(item.Id) && item.IsActive)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);

        var invalidCatalogIds = desiredCatalogIds
            .Except(validCatalogIds)
            .ToList();

        if (invalidCatalogIds.Count > 0)
        {
            var invalidErrors = invalidCatalogIds
                .Select(item => new ValidationError("invalidCatalogIds", item.ToString()))
                .ToList();
            invalidErrors.Add(new ValidationError("catalogIds", "One or more catalog ids are invalid or inactive"));
            return ServiceResult<AdminProductResult>.Failure(invalidErrors);
        }

        var efDbContext = (DbContext)_dbContext;
        await using var transaction = await BeginTransactionIfSupportedAsync(efDbContext, cancellationToken);

        var currentLinks = await _dbContext.ProductCatalogs
            .Where(item => item.ProductSku == parsedSku.Value)
            .ToListAsync(cancellationToken);

        var currentSet = currentLinks.Select(item => item.CatalogId).ToHashSet();
        var desiredSet = desiredCatalogIds.ToHashSet();

        var toRemove = currentLinks.Where(item => !desiredSet.Contains(item.CatalogId)).ToList();
        var toAdd = desiredCatalogIds.Where(item => !currentSet.Contains(item)).ToList();

        if (toRemove.Count > 0)
        {
            _dbContext.ProductCatalogs.RemoveRange(toRemove);
        }

        if (toAdd.Count > 0)
        {
            _dbContext.ProductCatalogs.AddRange(toAdd.Select(item => new ProductCatalog
            {
                CatalogId = item,
                ProductSku = parsedSku.Value,
                CreatedAt = DateTimeOffset.UtcNow
            }));
        }

        AddAuditEvent("AdminProducts.ReplaceCatalogs", actorUserId, tenant.Id, parsedSku.Value, new
        {
            Added = toAdd.Count,
            Removed = toRemove.Count,
            Tenant = tenant.Slug
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return await GetBySkuAsync(parsedSku.Value, cancellationToken);
    }

    private static string? NormalizeOptionalText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private async Task SyncListingDraftsFromAdminProductAsync(
        Product product,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var drafts = await _dbContext.ListingDrafts
                .Where(item => item.Provider == MarketplaceProvider.MercadoLivre
                               && item.BaseProductSku == product.Sku
                               && (item.Status == ListingDraftStatus.Draft
                                   || item.Status == ListingDraftStatus.Valid
                                   || item.Status == ListingDraftStatus.Error))
                .ToListAsync(cancellationToken);

            if (drafts.Count == 0)
            {
                return;
            }

            var variantBySku = await _dbContext.ProductVariants
                .AsNoTracking()
                .Where(item => item.BaseSku == product.Sku)
                .ToDictionaryAsync(item => item.VariantSku, item => item, StringComparer.Ordinal, cancellationToken);

            var imageUrls = await _dbContext.ProductImages
                .AsNoTracking()
                .Where(item => item.ProductSku == product.Sku)
                .OrderByDescending(item => item.IsPrimary)
                .ThenBy(item => item.SortOrder)
                .ThenBy(item => item.CreatedAt)
                .Select(item => item.Url)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Take(10)
                .ToListAsync(cancellationToken);

            var validCount = drafts.Count(item => item.Status == ListingDraftStatus.Valid);
            var draftCount = drafts.Count(item => item.Status == ListingDraftStatus.Draft);
            var errorCount = drafts.Count(item => item.Status == ListingDraftStatus.Error);
            var traceId = Activity.Current?.TraceId.ToString();

            _logger.LogInformation(
                "admin_product_draft_sync_started sku={Sku} affectedDrafts={AffectedDrafts} reason={Reason} statusDraft={StatusDraft} statusValid={StatusValid} statusError={StatusError} traceId={TraceId}",
                product.Sku,
                drafts.Count,
                reason,
                draftCount,
                validCount,
                errorCount,
                traceId);

            var changedDrafts = 0;
            foreach (var draft in drafts)
            {
                variantBySku.TryGetValue(draft.SabrVariantSku, out var variant);
                var providerDraft = ParseProviderDraftObject(draft.ProviderDraftJson);

                providerDraft["title"] = BuildDraftTitle(product.Name, variant?.Name, draft.SabrVariantSku);
                providerDraft["description"] = NormalizeOptionalText(product.Description);
                providerDraft["gtin"] = NormalizeOptionalText(product.Ean);
                providerDraft["ncm"] = NormalizeOptionalText(product.Ncm);
                providerDraft["origin"] = "Nacional";
                providerDraft["images"] = BuildProviderDraftImagesNode(imageUrls);
                providerDraft["productCostCents"] = ResolveDraftProductCostCents(product, variant);
                providerDraft["updatedAt"] = DateTimeOffset.UtcNow;

                if (!string.IsNullOrWhiteSpace(product.Ean))
                {
                    providerDraft["emptyGtinReason"] = null;
                }

                draft.ProviderDraftJson = providerDraft.ToJsonString();
                draft.UpdatedAt = DateTimeOffset.UtcNow;
                if (draft.Status == ListingDraftStatus.Valid)
                {
                    draft.Status = ListingDraftStatus.Draft;
                }

                changedDrafts += 1;
            }

            _logger.LogInformation(
                "admin_product_draft_sync_applied sku={Sku} affectedDrafts={AffectedDrafts} changedDrafts={ChangedDrafts} reason={Reason} statusDraft={StatusDraft} statusValid={StatusValid} statusError={StatusError} traceId={TraceId}",
                product.Sku,
                drafts.Count,
                changedDrafts,
                reason,
                draftCount,
                validCount,
                errorCount,
                traceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "admin_product_draft_sync_failed sku={Sku} reason={Reason} traceId={TraceId}",
                product.Sku,
                reason,
                Activity.Current?.TraceId.ToString());
            throw;
        }
    }

    private static JsonObject ParseProviderDraftObject(string? providerDraftJson)
    {
        if (string.IsNullOrWhiteSpace(providerDraftJson))
        {
            return new JsonObject();
        }

        try
        {
            var parsed = JsonNode.Parse(providerDraftJson);
            return parsed as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static JsonArray BuildProviderDraftImagesNode(IReadOnlyList<string> urls)
    {
        var result = new JsonArray();
        for (var index = 0; index < urls.Count; index++)
        {
            var url = urls[index]?.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            result.Add(new JsonObject
            {
                ["url"] = url,
                ["position"] = index + 1
            });
        }

        return result;
    }

    private static long ResolveDraftProductCostCents(Product product, ProductVariant? variant)
    {
        if (variant != null && variant.CostPriceCents > 0)
        {
            return variant.CostPriceCents;
        }

        return Math.Max(0, product.CostPriceCents);
    }

    private static string BuildDraftTitle(string? productName, string? variantName, string fallbackSku)
    {
        var normalizedProduct = string.IsNullOrWhiteSpace(productName) ? string.Empty : productName.Trim();
        var normalizedVariant = string.IsNullOrWhiteSpace(variantName) ? string.Empty : variantName.Trim();

        if (string.IsNullOrWhiteSpace(normalizedProduct) && string.IsNullOrWhiteSpace(normalizedVariant))
        {
            return fallbackSku;
        }

        if (string.IsNullOrWhiteSpace(normalizedVariant)
            || string.Equals(normalizedProduct, normalizedVariant, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(normalizedProduct) ? normalizedVariant : normalizedProduct;
        }

        return $"{normalizedProduct} - {normalizedVariant}";
    }

    private static (string Name, string Brand, string? Ncm, string? Ean, string? Description, string? CategoryId, string? ThumbnailUrl, decimal? WidthCm, decimal? HeightCm, decimal? LengthCm, decimal? WeightKg, bool RequiresAnatel, string? AnatelHomologationNumber, Guid? AnatelDocumentId, long CostPriceCents, long CatalogPriceCents, bool IsActive) BuildCandidate(
        Product product,
        AdminProductUpdateRequest request)
    {
        return (
            Name: request.Name?.Trim() ?? product.Name,
            Brand: request.Brand?.Trim() ?? product.Brand,
            Ncm: request.Ncm != null ? NormalizeOptionalText(request.Ncm) : product.Ncm,
            Ean: request.Ean != null ? NormalizeOptionalText(request.Ean) : product.Ean,
            Description: request.Description != null ? NormalizeOptionalText(request.Description) : product.Description,
            CategoryId: request.CategoryIdProvided ? NormalizeOptionalText(request.CategoryId) : product.CategoryId,
            ThumbnailUrl: request.ThumbnailUrl != null ? NormalizeOptionalText(request.ThumbnailUrl) : product.ThumbnailUrl,
            WidthCm: request.WidthCm ?? product.WidthCm,
            HeightCm: request.HeightCm ?? product.HeightCm,
            LengthCm: request.LengthCm ?? product.LengthCm,
            WeightKg: request.WeightKg ?? product.WeightKg,
            RequiresAnatel: request.RequiresAnatel ?? product.RequiresAnatel,
            AnatelHomologationNumber: request.AnatelHomologationNumber != null
                ? NormalizeOptionalText(request.AnatelHomologationNumber)
                : product.AnatelHomologationNumber,
            AnatelDocumentId: request.AnatelDocumentId ?? product.AnatelDocumentId,
            CostPriceCents: request.CostPriceCents ?? product.CostPriceCents,
            CatalogPriceCents: request.CatalogPriceCents ?? product.CatalogPriceCents,
            IsActive: request.IsActive ?? product.IsActive
        );
    }

    private static void ApplyCandidate(
        Product product,
        (string Name, string Brand, string? Ncm, string? Ean, string? Description, string? CategoryId, string? ThumbnailUrl, decimal? WidthCm, decimal? HeightCm, decimal? LengthCm, decimal? WeightKg, bool RequiresAnatel, string? AnatelHomologationNumber, Guid? AnatelDocumentId, long CostPriceCents, long CatalogPriceCents, bool IsActive) candidate)
    {
        product.Name = candidate.Name;
        product.Brand = candidate.Brand;
        product.Ncm = candidate.Ncm;
        product.Ean = candidate.Ean;
        product.Description = candidate.Description;
        product.CategoryId = candidate.CategoryId;
        product.ThumbnailUrl = candidate.ThumbnailUrl;
        product.WidthCm = candidate.WidthCm;
        product.HeightCm = candidate.HeightCm;
        product.LengthCm = candidate.LengthCm;
        product.WeightKg = candidate.WeightKg;
        product.RequiresAnatel = candidate.RequiresAnatel;
        product.AnatelHomologationNumber = candidate.AnatelHomologationNumber;
        product.AnatelDocumentId = candidate.AnatelDocumentId;
        product.CostPriceCents = candidate.CostPriceCents;
        product.CatalogPriceCents = candidate.CatalogPriceCents;
        product.IsActive = candidate.IsActive;
    }

    private static void ApplyUpsertValues(Product product, AdminProductUpsertRequest request, string resolvedCategorySlug)
    {
        product.Name = request.Name.Trim();
        product.Brand = request.Brand.Trim();
        product.Ncm = NormalizeOptionalText(request.Ncm);
        product.Ean = NormalizeOptionalText(request.Ean);
        product.Description = NormalizeOptionalText(request.Description);
        product.CategoryId = resolvedCategorySlug;
        product.ThumbnailUrl = NormalizeOptionalText(request.ThumbnailUrl);
        product.WidthCm = request.WidthCm;
        product.HeightCm = request.HeightCm;
        product.LengthCm = request.LengthCm;
        product.WeightKg = request.WeightKg;
        product.RequiresAnatel = request.RequiresAnatel;
        product.AnatelHomologationNumber = NormalizeOptionalText(request.AnatelHomologationNumber);
        product.AnatelDocumentId = request.AnatelDocumentId;
        product.CostPriceCents = request.CostPriceCents;
        product.CatalogPriceCents = request.CatalogPriceCents;
        product.IsActive = request.IsActive;
    }

    private static List<ValidationError> ValidateUpsertRequest(AdminProductUpsertRequest request)
    {
        var errors = new List<ValidationError>();
        if (string.IsNullOrWhiteSpace(request.Sku))
        {
            errors.Add(new ValidationError("sku", "SKU is required"));
        }
        else if (!Sku.TryParse(request.Sku, out _))
        {
            errors.Add(new ValidationError("sku", "SKU format is invalid"));
        }

        errors.AddRange(ValidateProductFields((
            Name: request.Name?.Trim() ?? string.Empty,
            Brand: request.Brand?.Trim() ?? string.Empty,
            Ncm: NormalizeOptionalText(request.Ncm),
            Ean: NormalizeOptionalText(request.Ean),
            Description: NormalizeOptionalText(request.Description),
            CategoryId: NormalizeOptionalText(request.CategoryId),
            ThumbnailUrl: NormalizeOptionalText(request.ThumbnailUrl),
            WidthCm: request.WidthCm,
            HeightCm: request.HeightCm,
            LengthCm: request.LengthCm,
            WeightKg: request.WeightKg,
            RequiresAnatel: request.RequiresAnatel,
            AnatelHomologationNumber: NormalizeOptionalText(request.AnatelHomologationNumber),
            AnatelDocumentId: request.AnatelDocumentId,
            CostPriceCents: request.CostPriceCents,
            CatalogPriceCents: request.CatalogPriceCents,
            IsActive: request.IsActive
        )));

        return errors;
    }

    private static List<ValidationError> ValidateProductFields(
        (string Name, string Brand, string? Ncm, string? Ean, string? Description, string? CategoryId, string? ThumbnailUrl, decimal? WidthCm, decimal? HeightCm, decimal? LengthCm, decimal? WeightKg, bool RequiresAnatel, string? AnatelHomologationNumber, Guid? AnatelDocumentId, long CostPriceCents, long CatalogPriceCents, bool IsActive) values)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(values.Name))
        {
            errors.Add(new ValidationError("name", "Name is required"));
        }
        else if (values.Name.Length > 250)
        {
            errors.Add(new ValidationError("name", "Name cannot exceed 250 characters"));
        }

        if (string.IsNullOrWhiteSpace(values.Brand))
        {
            errors.Add(new ValidationError("brand", "Brand is required"));
        }
        else if (values.Brand.Length > 120)
        {
            errors.Add(new ValidationError("brand", "Brand cannot exceed 120 characters"));
        }

        if (!string.IsNullOrWhiteSpace(values.Ncm) && !NcmRegex.IsMatch(values.Ncm))
        {
            errors.Add(new ValidationError("ncm", "NCM must contain exactly 8 digits"));
        }

        if (!string.IsNullOrWhiteSpace(values.Ean) && !EanRegex.IsMatch(values.Ean))
        {
            errors.Add(new ValidationError("ean", "EAN/GTIN must contain 8, 12, 13 or 14 digits"));
        }

        if (!string.IsNullOrWhiteSpace(values.Description) && values.Description.Length > 4000)
        {
            errors.Add(new ValidationError("description", "Description cannot exceed 4000 characters"));
        }

        if (!string.IsNullOrWhiteSpace(values.CategoryId) && values.CategoryId.Length > 120)
        {
            errors.Add(new ValidationError("categoryId", "CategoryId cannot exceed 120 characters"));
        }
        else if (!string.IsNullOrWhiteSpace(values.CategoryId) &&
                 !CategorySlugRegex.IsMatch(values.CategoryId.Trim().ToLowerInvariant()))
        {
            errors.Add(new ValidationError("categoryId", "Category slug format is invalid"));
        }

        if (!string.IsNullOrWhiteSpace(values.ThumbnailUrl) && values.ThumbnailUrl.Length > 500)
        {
            errors.Add(new ValidationError("thumbnailUrl", "ThumbnailUrl cannot exceed 500 characters"));
        }

        if (values.WidthCm.HasValue && values.WidthCm.Value < 0)
        {
            errors.Add(new ValidationError("widthCm", "Width must be greater than or equal to zero"));
        }

        if (values.HeightCm.HasValue && values.HeightCm.Value < 0)
        {
            errors.Add(new ValidationError("heightCm", "Height must be greater than or equal to zero"));
        }

        if (values.LengthCm.HasValue && values.LengthCm.Value < 0)
        {
            errors.Add(new ValidationError("lengthCm", "Length must be greater than or equal to zero"));
        }

        if (values.WeightKg.HasValue && values.WeightKg.Value < 0)
        {
            errors.Add(new ValidationError("weightKg", "Weight must be greater than or equal to zero"));
        }

        if (values.CostPriceCents < 0)
        {
            errors.Add(new ValidationError("costPriceCents", "Cost price cannot be negative"));
        }

        if (values.CatalogPriceCents < 0)
        {
            errors.Add(new ValidationError("catalogPriceCents", "Catalog price cannot be negative"));
        }

        if (values.RequiresAnatel && string.IsNullOrWhiteSpace(values.AnatelHomologationNumber))
        {
            errors.Add(new ValidationError("anatelHomologationNumber", "ANATEL homologation number is required"));
        }

        if (!string.IsNullOrWhiteSpace(values.AnatelHomologationNumber) &&
            !AnatelRegex.IsMatch(values.AnatelHomologationNumber))
        {
            errors.Add(new ValidationError("anatelHomologationNumber", "ANATEL homologation number format is invalid"));
        }

        return errors;
    }

    private async Task<List<ValidationError>> ValidateActivationAsync(
        string sku,
        CancellationToken cancellationToken)
    {
        var errors = new List<ValidationError>();

        var hasCatalogLink = await _dbContext.ProductCatalogs
            .AsNoTracking()
            .AnyAsync(item => item.ProductSku == sku, cancellationToken);

        if (!hasCatalogLink)
        {
            errors.Add(new ValidationError("catalogLinks", "Active product must be linked to at least one catalog"));
        }

        return errors;
    }

    private async Task<ServiceResult<string>> ResolveCategoryForUpsertAsync(
        string? requestedCategoryId,
        CancellationToken cancellationToken)
    {
        var desiredSlug = string.IsNullOrWhiteSpace(requestedCategoryId)
            ? UncategorizedSlug
            : requestedCategoryId.Trim().ToLowerInvariant();

        return await ResolveActiveCategoryBySlugAsync(desiredSlug, cancellationToken);
    }

    private async Task<ServiceResult<string>> ResolveCategoryForPatchAsync(
        string? currentCategoryId,
        string? requestedCategoryId,
        bool categoryIdProvided,
        CancellationToken cancellationToken)
    {
        string desiredSlug;
        if (categoryIdProvided)
        {
            desiredSlug = string.IsNullOrWhiteSpace(requestedCategoryId)
                ? UncategorizedSlug
                : requestedCategoryId.Trim().ToLowerInvariant();
        }
        else
        {
            desiredSlug = string.IsNullOrWhiteSpace(currentCategoryId)
                ? UncategorizedSlug
                : currentCategoryId.Trim().ToLowerInvariant();
        }

        return await ResolveActiveCategoryBySlugAsync(desiredSlug, cancellationToken);
    }

    private async Task<ServiceResult<string>> ResolveActiveCategoryBySlugAsync(
        string categorySlug,
        CancellationToken cancellationToken)
    {
        if (!CategorySlugRegex.IsMatch(categorySlug))
        {
            return ServiceResult<string>.Failure(new[]
            {
                new ValidationError("categoryId", "Category slug format is invalid")
            });
        }

        var category = await _dbContext.Categories
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Slug == categorySlug, cancellationToken);

        if (category == null && string.Equals(categorySlug, UncategorizedSlug, StringComparison.Ordinal))
        {
            var fallbackCategory = new Category
            {
                Name = "Sem Categoria",
                Slug = UncategorizedSlug,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            _dbContext.Categories.Add(fallbackCategory);
            await _dbContext.SaveChangesAsync(cancellationToken);

            category = fallbackCategory;
        }

        if (category == null)
        {
            return ServiceResult<string>.Failure(new[]
            {
                new ValidationError("categoryId", "Category not found")
            });
        }

        if (!category.IsActive)
        {
            return ServiceResult<string>.Failure(new[]
            {
                new ValidationError("categoryId", "Category inactive")
            });
        }

        return ServiceResult<string>.Success(category.Slug);
    }

    private async Task<ServiceResult<Tenant>> ResolveTenantAsync(string tenantSlug, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantSlug))
        {
            return ServiceResult<Tenant>.Failure(new[]
            {
                new ValidationError("tenantId", "Tenant slug is required")
            });
        }

        var normalizedSlug = tenantSlug.Trim().ToLowerInvariant();
        var tenant = await _dbContext.Tenants.FirstOrDefaultAsync(
            item => item.Slug == normalizedSlug,
            cancellationToken);

        if (tenant == null)
        {
            return ServiceResult<Tenant>.Failure(new[]
            {
                new ValidationError("tenantId", "Tenant not found")
            });
        }

        if (tenant.Status != TenantStatus.Active)
        {
            return ServiceResult<Tenant>.Failure(new[]
            {
                new ValidationError("tenantStatus", "Tenant inactive")
            });
        }

        return ServiceResult<Tenant>.Success(tenant);
    }

    private void AddAuditEvent(string action, Guid actorUserId, string tenantId, string sku, object metadata)
    {
        _dbContext.AuditEvents.Add(new AuditEvent
        {
            TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
            ActorType = "AdminUser",
            ActorId = actorUserId,
            Action = action,
            Entity = nameof(Product),
            EntityId = null,
            RequestId = Guid.NewGuid(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                sku,
                metadata
            })
        });
    }

    private static AdminProductResult MapToAdminResult(Product product, IReadOnlyCollection<ProductImage> images)
    {
        return new AdminProductResult
        {
            Sku = product.Sku,
            Name = product.Name,
            Brand = product.Brand,
            Ncm = product.Ncm,
            Ean = product.Ean,
            Description = product.Description,
            CategoryId = product.CategoryId,
            ThumbnailUrl = product.ThumbnailUrl,
            WidthCm = product.WidthCm,
            HeightCm = product.HeightCm,
            LengthCm = product.LengthCm,
            WeightKg = product.WeightKg,
            RequiresAnatel = product.RequiresAnatel,
            AnatelHomologationNumber = product.AnatelHomologationNumber,
            AnatelDocumentId = product.AnatelDocumentId,
            CostPriceCents = product.CostPriceCents,
            CatalogPriceCents = product.CatalogPriceCents,
            IsActive = product.IsActive,
            Images = images.Select(MapImage).ToList(),
            CreatedAt = product.CreatedAt,
            UpdatedAt = product.UpdatedAt
        };
    }

    private static AdminProductResult MapToAdminResultWithoutImages(Product product)
    {
        return new AdminProductResult
        {
            Sku = product.Sku,
            Name = product.Name,
            Brand = product.Brand,
            Ncm = product.Ncm,
            Ean = product.Ean,
            Description = product.Description,
            CategoryId = product.CategoryId,
            ThumbnailUrl = product.ThumbnailUrl,
            WidthCm = product.WidthCm,
            HeightCm = product.HeightCm,
            LengthCm = product.LengthCm,
            WeightKg = product.WeightKg,
            RequiresAnatel = product.RequiresAnatel,
            AnatelHomologationNumber = product.AnatelHomologationNumber,
            AnatelDocumentId = product.AnatelDocumentId,
            CostPriceCents = product.CostPriceCents,
            CatalogPriceCents = product.CatalogPriceCents,
            IsActive = product.IsActive,
            Images = Array.Empty<ProductImageResult>(),
            CreatedAt = product.CreatedAt,
            UpdatedAt = product.UpdatedAt
        };
    }

    private static ProductImageResult MapImage(ProductImage image)
    {
        return new ProductImageResult
        {
            Id = image.Id,
            ProductSku = image.ProductSku,
            Url = image.Url,
            MimeType = image.MimeType,
            SizeBytes = image.SizeBytes,
            SortOrder = image.SortOrder,
            IsPrimary = image.IsPrimary,
            CreatedAt = image.CreatedAt
        };
    }

    private static ProductPricingUpdateResult MapToPricingResult(Product product)
    {
        return new ProductPricingUpdateResult
        {
            Sku = product.Sku,
            CostPriceCents = product.CostPriceCents,
            CatalogPriceCents = product.CatalogPriceCents,
            UpdatedAt = product.UpdatedAt
        };
    }

    private static async Task<IDbContextTransaction?> BeginTransactionIfSupportedAsync(
        DbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            return null;
        }

        return await dbContext.Database.BeginTransactionAsync(cancellationToken);
    }
}
