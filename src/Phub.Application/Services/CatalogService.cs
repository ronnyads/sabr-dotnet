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

        var query =
            from variant in _dbContext.ProductVariants.AsNoTracking()
            join product in _dbContext.Products.AsNoTracking() on variant.BaseSku equals product.Sku
            where variant.IsActive
                  && product.IsActive
                  && allowedSkuQuery.Contains(variant.BaseSku)
            select new { variant, product };

        if (!string.IsNullOrWhiteSpace(productSku))
        {
            var normalizedProductSku = Phub.Domain.ValueObjects.Sku.Normalize(productSku);
            query = query.Where(item => item.variant.BaseSku == normalizedProductSku);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            query = query.Where(item =>
                item.variant.VariantSku.ToUpper().Contains(term) ||
                item.variant.BaseSku.ToUpper().Contains(term) ||
                item.variant.Name.ToUpper().Contains(term) ||
                item.product.Name.ToUpper().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(item => item.product.Name)
            .ThenBy(item => item.variant.Name)
            .ThenBy(item => item.variant.VariantSku)
            .Skip(skip)
            .Take(limit)
            .Select(item => new CatalogVariantDto
            {
                VariantSku = item.variant.VariantSku,
                BaseSku = item.variant.BaseSku,
                ProductName = item.product.Name,
                VariantName = item.variant.Name,
                AvailableStock = item.variant.AvailableStock,
                ThumbnailUrl = item.product.ThumbnailUrl
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<CatalogVariantDto>
        {
            Items = items,
            Total = total,
            Skip = skip,
            Limit = limit
        };
    }
}
