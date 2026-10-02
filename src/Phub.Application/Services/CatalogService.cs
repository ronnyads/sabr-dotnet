using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Enums;
using Phub.Domain.ValueObjects;

namespace Phub.Application.Services;

public sealed class CatalogService
{
    private readonly IAppDbContext _dbContext;
    private readonly CatalogAuthorizationService _catalogAuthorization;

    public CatalogService(IAppDbContext dbContext, CatalogAuthorizationService catalogAuthorization)
    {
        _dbContext = dbContext;
        _catalogAuthorization = catalogAuthorization;
    }

    public async Task<CatalogProductPageDto> GetProductsAsync(
        string tenantId,
        Guid clientId,
        int skip,
        int limit,
        string? search,
        string? categoryId,
        string? brand,
        string stockStatus,
        string membership,
        string sort,
        string direction,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var allowedSkuQuery = _catalogAuthorization.GetAllowedSkuQuery(tenantId, clientId, now);
        var addedSkuQuery = _dbContext.Publications
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.ClientId == clientId && item.Status == PublicationStatus.Draft)
            .Select(item => item.ProductSku);

        var authorizedQuery = _dbContext.Products
            .AsNoTracking()
            .Where(product => product.IsActive
                              && allowedSkuQuery.Contains(product.Sku)
                              && !product.Sku.StartsWith("MLB"));

        var facets = await BuildFacetsAsync(authorizedQuery, addedSkuQuery, cancellationToken);
        var query = authorizedQuery;

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            query = query.Where(product =>
                product.Sku.ToUpper().Contains(term) ||
                product.Name.ToUpper().Contains(term) ||
                product.Brand.ToUpper().Contains(term) ||
                _dbContext.Categories.Any(category =>
                    category.IsActive &&
                    (product.CategoryId == category.Id.ToString() || product.CategoryId == category.Slug) &&
                    category.Name.ToUpper().Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            var normalizedCategory = categoryId.Trim();
            query = query.Where(product => product.CategoryId == normalizedCategory);
        }

        if (!string.IsNullOrWhiteSpace(brand))
        {
            var normalizedBrand = brand.Trim().ToUpperInvariant();
            query = query.Where(product => product.Brand.ToUpper() == normalizedBrand);
        }

        if (stockStatus == "IN_STOCK")
        {
            query = query.Where(product => _dbContext.ProductVariants
                .Where(variant => variant.BaseSku == product.Sku && variant.IsActive)
                .Sum(variant => (int?)variant.AvailableStock) > 0);
        }
        else if (stockStatus == "OUT_OF_STOCK")
        {
            query = query.Where(product => (_dbContext.ProductVariants
                .Where(variant => variant.BaseSku == product.Sku && variant.IsActive)
                .Sum(variant => (int?)variant.AvailableStock) ?? 0) <= 0);
        }

        if (membership == "ADDED")
        {
            query = query.Where(product => addedSkuQuery.Contains(product.Sku));
        }
        else if (membership == "NOT_ADDED")
        {
            query = query.Where(product => !addedSkuQuery.Contains(product.Sku));
        }

        var total = await query.CountAsync(cancellationToken);
        var orderedQuery = ApplyOrdering(query, search, sort, direction);
        var items = await orderedQuery
            .Skip(skip)
            .Take(limit)
            .Select(product => new CatalogProductDto
            {
                Sku = product.Sku,
                Name = product.Name,
                ThumbnailUrl = product.ThumbnailUrl,
                CatalogPriceCents = product.CatalogPriceCents,
                AvailableStock = _dbContext.ProductVariants
                    .Where(variant => variant.BaseSku == product.Sku && variant.IsActive)
                    .Sum(variant => (int?)variant.AvailableStock) ?? 0,
                IsActive = product.IsActive,
                Brand = product.Brand,
                CategoryId = product.CategoryId,
                CategoryName = _dbContext.Categories
                    .Where(category => category.IsActive &&
                        (product.CategoryId == category.Id.ToString() || product.CategoryId == category.Slug))
                    .Select(category => category.Name)
                    .FirstOrDefault(),
                VariantCount = _dbContext.ProductVariants.Count(variant => variant.BaseSku == product.Sku && variant.IsActive),
                CreatedAt = product.CreatedAt,
                IsAddedToMyProducts = addedSkuQuery.Contains(product.Sku)
            })
            .ToListAsync(cancellationToken);

        return new CatalogProductPageDto
        {
            Items = items,
            Total = total,
            Skip = skip,
            Limit = limit,
            Facets = facets
        };
    }

    public async Task<CatalogProductDetailDto?> GetProductAsync(
        string tenantId,
        Guid clientId,
        string sku,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sku) || InternalCatalogSkuPolicy.IsMarketplaceExternalIdentifier(sku))
        {
            return null;
        }

        var normalizedSku = Sku.Normalize(sku);
        var allowedSkuQuery = _catalogAuthorization.GetAllowedSkuQuery(tenantId, clientId, DateTimeOffset.UtcNow);
        var product = await _dbContext.Products.AsNoTracking()
            .Where(item => item.IsActive && item.Sku == normalizedSku && allowedSkuQuery.Contains(item.Sku))
            .Select(item => new
            {
                item.Sku,
                item.Name,
                item.Brand,
                item.Description,
                item.CategoryId,
                item.Ncm,
                item.Ean,
                item.CatalogPriceCents,
                item.WidthCm,
                item.HeightCm,
                item.LengthCm,
                item.WeightKg,
                item.RequiresAnatel,
                item.AnatelHomologationNumber
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (product == null)
        {
            return null;
        }

        var variants = await _dbContext.ProductVariants.AsNoTracking()
            .Where(item => item.BaseSku == normalizedSku && item.IsActive)
            .OrderBy(item => item.Name)
            .ThenBy(item => item.VariantSku)
            .Select(item => new CatalogProductVariantDto
            {
                Sku = item.VariantSku,
                Name = item.Name,
                AvailableStock = item.AvailableStock,
                CatalogPriceCents = item.CatalogPriceCents > 0 ? item.CatalogPriceCents : product.CatalogPriceCents
            })
            .ToListAsync(cancellationToken);
        var images = await _dbContext.ProductImages.AsNoTracking()
            .Where(item => item.ProductSku == normalizedSku)
            .OrderByDescending(item => item.IsPrimary)
            .ThenBy(item => item.SortOrder)
            .Select(item => new CatalogProductImageDto
            {
                Url = item.Url,
                Position = item.SortOrder,
                IsPrimary = item.IsPrimary
            })
            .ToListAsync(cancellationToken);
        var categoryName = await _dbContext.Categories.AsNoTracking()
            .Where(item => item.IsActive &&
                (product.CategoryId == item.Id.ToString() || product.CategoryId == item.Slug))
            .Select(item => item.Name)
            .FirstOrDefaultAsync(cancellationToken);
        var isAdded = await _dbContext.Publications.AsNoTracking()
            .AnyAsync(item => item.TenantId == tenantId && item.ClientId == clientId &&
                              item.Status == PublicationStatus.Draft && item.ProductSku == normalizedSku,
                cancellationToken);

        return new CatalogProductDetailDto
        {
            Sku = product.Sku,
            Name = product.Name,
            Brand = product.Brand,
            Description = product.Description,
            CategoryId = product.CategoryId,
            CategoryName = categoryName,
            Ncm = product.Ncm,
            Ean = product.Ean,
            CatalogPriceCents = product.CatalogPriceCents,
            AvailableStock = variants.Sum(item => item.AvailableStock),
            IsAddedToMyProducts = isAdded,
            WidthCm = product.WidthCm,
            HeightCm = product.HeightCm,
            LengthCm = product.LengthCm,
            WeightKg = product.WeightKg,
            RequiresAnatel = product.RequiresAnatel,
            AnatelHomologationNumber = product.AnatelHomologationNumber,
            Images = images,
            Variants = variants
        };
    }

    private async Task<CatalogProductFacetsDto> BuildFacetsAsync(
        IQueryable<Phub.Domain.Entities.Product> query,
        IQueryable<string> addedSkuQuery,
        CancellationToken cancellationToken)
    {
        var categories = await query
            .Where(product => product.CategoryId != null && product.CategoryId != "")
            .GroupBy(product => product.CategoryId!)
            .Select(group => new { Value = group.Key, Count = group.Count() })
            .OrderBy(item => item.Value)
            .ToListAsync(cancellationToken);
        var categoryIds = categories.Select(item => item.Value).ToList();
        var categoryLabels = await _dbContext.Categories.AsNoTracking()
            .Where(item => item.IsActive && (categoryIds.Contains(item.Id.ToString()) || categoryIds.Contains(item.Slug)))
            .Select(item => new { item.Id, item.Slug, item.Name })
            .ToListAsync(cancellationToken);
        var categoryMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categoryLabels)
        {
            categoryMap[category.Id.ToString()] = category.Name;
            categoryMap[category.Slug] = category.Name;
        }

        var brands = await query
            .Where(product => product.Brand != "")
            .GroupBy(product => product.Brand)
            .Select(group => new CatalogFacetOptionDto
            {
                Value = group.Key,
                Label = group.Key,
                Count = group.Count()
            })
            .OrderBy(item => item.Label)
            .ToListAsync(cancellationToken);
        var inStockCount = await query.CountAsync(product => _dbContext.ProductVariants
            .Where(variant => variant.BaseSku == product.Sku && variant.IsActive)
            .Sum(variant => (int?)variant.AvailableStock) > 0, cancellationToken);
        var total = await query.CountAsync(cancellationToken);
        var addedCount = await query.CountAsync(product => addedSkuQuery.Contains(product.Sku), cancellationToken);

        return new CatalogProductFacetsDto
        {
            Categories = categories.Select(item => new CatalogFacetOptionDto
            {
                Value = item.Value,
                Label = categoryMap.TryGetValue(item.Value, out var label) ? label : item.Value,
                Count = item.Count
            }).OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToList(),
            Brands = brands,
            InStockCount = inStockCount,
            OutOfStockCount = total - inStockCount,
            AddedCount = addedCount,
            NotAddedCount = total - addedCount
        };
    }

    private IQueryable<Phub.Domain.Entities.Product> ApplyOrdering(
        IQueryable<Phub.Domain.Entities.Product> query,
        string? search,
        string sort,
        string direction)
    {
        var descending = direction == "DESC";
        return sort switch
        {
            "NEWEST" => descending
                ? query.OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Sku)
                : query.OrderBy(item => item.CreatedAt).ThenBy(item => item.Sku),
            "PRICE" => descending
                ? query.OrderByDescending(item => item.CatalogPriceCents).ThenBy(item => item.Name)
                : query.OrderBy(item => item.CatalogPriceCents).ThenBy(item => item.Name),
            "STOCK" => descending
                ? query.OrderByDescending(item => _dbContext.ProductVariants
                        .Where(variant => variant.BaseSku == item.Sku && variant.IsActive)
                        .Sum(variant => (int?)variant.AvailableStock) ?? 0)
                    .ThenBy(item => item.Name)
                : query.OrderBy(item => _dbContext.ProductVariants
                        .Where(variant => variant.BaseSku == item.Sku && variant.IsActive)
                        .Sum(variant => (int?)variant.AvailableStock) ?? 0)
                    .ThenBy(item => item.Name),
            "RELEVANCE" when !string.IsNullOrWhiteSpace(search) => query
                .OrderByDescending(item => item.Sku.ToUpper() == search.Trim().ToUpperInvariant())
                .ThenByDescending(item => item.Name.ToUpper().StartsWith(search.Trim().ToUpperInvariant()))
                .ThenBy(item => item.Name)
                .ThenBy(item => item.Sku),
            _ => descending
                ? query.OrderByDescending(item => item.Name).ThenByDescending(item => item.Sku)
                : query.OrderBy(item => item.Name).ThenBy(item => item.Sku)
        };
    }

    public async Task<PagedResult<CatalogVariantDto>> GetVariantsAsync(
        string tenantId,
        Guid clientId,
        int skip,
        int limit,
        string? search,
        string? productSku,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var allowedSkuQuery = _catalogAuthorization.GetAllowedSkuQuery(tenantId, clientId, now);
        var variantsQuery =
            from variant in _dbContext.ProductVariants.AsNoTracking()
            join product in _dbContext.Products.AsNoTracking() on variant.BaseSku equals product.Sku
            where variant.IsActive
                  && product.IsActive
                  && !variant.VariantSku.StartsWith("MLB")
                  && !variant.BaseSku.StartsWith("MLB")
                  && !product.Sku.StartsWith("MLB")
                  && allowedSkuQuery.Contains(variant.BaseSku)
            select new { variant, product };

        if (!string.IsNullOrWhiteSpace(productSku))
        {
            var normalizedProductSku = Phub.Domain.ValueObjects.Sku.Normalize(productSku);
            variantsQuery = variantsQuery.Where(item => item.variant.BaseSku == normalizedProductSku);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            variantsQuery = variantsQuery.Where(item =>
                item.variant.VariantSku.ToUpper().Contains(term) ||
                item.variant.BaseSku.ToUpper().Contains(term) ||
                item.variant.Name.ToUpper().Contains(term) ||
                item.product.Name.ToUpper().Contains(term));
        }

        var variantItems = await variantsQuery
            .Select(item => new CatalogVariantDto
            {
                VariantSku = item.variant.VariantSku,
                BaseSku = item.variant.BaseSku,
                ProductName = item.product.Name,
                VariantName = item.variant.Name,
                AvailableStock = item.variant.AvailableStock,
                ThumbnailUrl = item.product.ThumbnailUrl,
                IsDefaultVariant = item.variant.VariantSku == item.variant.BaseSku
            })
            .ToListAsync(cancellationToken);

        var productsWithoutVariantsQuery = _dbContext.Products
            .AsNoTracking()
            .Where(product => product.IsActive
                              && !product.Sku.StartsWith("MLB")
                              && allowedSkuQuery.Contains(product.Sku))
            .Where(product => !_dbContext.ProductVariants.Any(variant => variant.BaseSku == product.Sku && variant.IsActive));

        if (!string.IsNullOrWhiteSpace(productSku))
        {
            var normalizedProductSku = Phub.Domain.ValueObjects.Sku.Normalize(productSku);
            productsWithoutVariantsQuery = productsWithoutVariantsQuery.Where(product => product.Sku == normalizedProductSku);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            productsWithoutVariantsQuery = productsWithoutVariantsQuery.Where(product =>
                product.Sku.ToUpper().Contains(term) ||
                product.Name.ToUpper().Contains(term));
        }

        var productFallbackItems = await productsWithoutVariantsQuery
            .Select(product => new CatalogVariantDto
            {
                VariantSku = product.Sku,
                BaseSku = product.Sku,
                ProductName = product.Name,
                VariantName = product.Name,
                AvailableStock = 0,
                ThumbnailUrl = product.ThumbnailUrl,
                IsDefaultVariant = true
            })
            .ToListAsync(cancellationToken);

        var allItems = variantItems
            .Concat(productFallbackItems)
            .OrderBy(item => item.ProductName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.IsDefaultVariant ? 0 : 1)
            .ThenBy(item => item.VariantName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.VariantSku, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PagedResult<CatalogVariantDto>
        {
            Items = allItems.Skip(skip).Take(limit).ToList(),
            Total = allItems.Count,
            Skip = skip,
            Limit = limit
        };
    }
}
