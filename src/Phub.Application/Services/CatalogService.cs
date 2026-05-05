using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;

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

    public async Task<PagedResult<CatalogProductDto>> GetProductsAsync(
        string tenantId,
        Guid clientId,
        int skip,
        int limit,
        string? search,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var allowedSkuQuery = _catalogAuthorization.GetAllowedSkuQuery(tenantId, clientId, now);

        var query = _dbContext.Products
            .AsNoTracking()
            .Where(product => product.IsActive && allowedSkuQuery.Contains(product.Sku));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            query = query.Where(product =>
                product.Sku.ToUpper().Contains(term) ||
                product.Name.ToUpper().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(product => product.Name)
            .ThenBy(product => product.Sku)
            .Skip(skip)
            .Take(limit)
            .Select(product => new CatalogProductDto
            {
                Sku = product.Sku,
                Name = product.Name,
                ThumbnailUrl = product.ThumbnailUrl,
                CatalogPriceCents = product.CatalogPriceCents,
                IsActive = product.IsActive
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<CatalogProductDto>
        {
            Items = items,
            Total = total,
            Skip = skip,
            Limit = limit
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
            .Where(product => product.IsActive && allowedSkuQuery.Contains(product.Sku))
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
