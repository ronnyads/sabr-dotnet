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
}
