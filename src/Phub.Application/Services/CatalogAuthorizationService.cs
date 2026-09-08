using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Domain.Enums;
using Phub.Domain.ValueObjects;

namespace Phub.Application.Services;

public sealed class CatalogAuthorizationService
{
    private readonly IAppDbContext _dbContext;

    public CatalogAuthorizationService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> IsSkuAllowedAsync(string tenantId, Guid clientId, string sku, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty || !Sku.TryParse(sku, out var normalizedSku))
        {
            return false;
        }
        return await GetAllowedSkuQuery(tenantId, clientId, DateTimeOffset.UtcNow)
            .AnyAsync(value => value == normalizedSku.Value, cancellationToken);
    }

    public IQueryable<string> GetAllowedSkuQuery(string tenantId, Guid clientId, DateTimeOffset now)
    {
        var approvedClientExists = _dbContext.Clients.Any(client =>
            client.TenantId == tenantId
            && client.Id == clientId
            && client.Status == ClientStatus.Approved);

        var publicSkus =
            from catalog in _dbContext.Catalogs
            join productCatalog in _dbContext.ProductCatalogs on catalog.Id equals productCatalog.CatalogId
            where approvedClientExists
                  && catalog.IsActive
                  && catalog.AccessMode == CatalogAccessMode.Public
            select productCatalog.ProductSku;

        var planRestrictedSkus =
            (from subscription in _dbContext.ClientPlanSubscriptions
             join plan in _dbContext.Plans on subscription.PlanId equals plan.Id
             join planCatalog in _dbContext.PlanCatalogs on subscription.PlanId equals planCatalog.PlanId
             join catalog in _dbContext.Catalogs on planCatalog.CatalogId equals catalog.Id
             join productCatalog in _dbContext.ProductCatalogs on planCatalog.CatalogId equals productCatalog.CatalogId
             where subscription.TenantId == tenantId
                   && subscription.ClientId == clientId
                   && subscription.IsActive
                   && plan.IsActive
                   && catalog.IsActive
                   && catalog.AccessMode == CatalogAccessMode.PlanRestricted
                   && subscription.StartsAt <= now
                   && subscription.EndsAt.HasValue
                   && now < subscription.EndsAt.Value
             select productCatalog.ProductSku);

        return publicSkus.Concat(planRestrictedSkus).Distinct();
    }
}
