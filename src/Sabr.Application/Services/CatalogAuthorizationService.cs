using Microsoft.EntityFrameworkCore;
using Sabr.Application.Abstractions;
using Sabr.Domain.ValueObjects;

namespace Sabr.Application.Services;

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
        var now = DateTimeOffset.UtcNow;

        var allowedQuery =
            from subscription in _dbContext.ClientPlanSubscriptions
            join plan in _dbContext.Plans on new { subscription.TenantId, PlanId = subscription.PlanId }
                equals new { plan.TenantId, PlanId = plan.Id }
            join planCatalog in _dbContext.PlanCatalogs on new { subscription.TenantId, subscription.PlanId }
                equals new { planCatalog.TenantId, planCatalog.PlanId }
            join catalog in _dbContext.Catalogs on new { planCatalog.TenantId, CatalogId = planCatalog.CatalogId }
                equals new { catalog.TenantId, CatalogId = catalog.Id }
            join productCatalog in _dbContext.ProductCatalogs on new { planCatalog.TenantId, planCatalog.CatalogId }
                equals new { productCatalog.TenantId, productCatalog.CatalogId }
            where subscription.TenantId == tenantId
                  && subscription.ClientId == clientId
                  && subscription.IsActive
                  && plan.IsActive
                  && catalog.IsActive
                  && subscription.StartsAt <= now
                  && subscription.EndsAt.HasValue
                  && now < subscription.EndsAt.Value
                  && productCatalog.ProductSku == normalizedSku.Value
            select productCatalog.ProductSku;

        return await allowedQuery.AnyAsync(cancellationToken);
    }

    public IQueryable<string> GetAllowedSkuQuery(string tenantId, Guid clientId, DateTimeOffset now)
    {
        return
            (from subscription in _dbContext.ClientPlanSubscriptions
             join plan in _dbContext.Plans on new { subscription.TenantId, PlanId = subscription.PlanId }
                 equals new { plan.TenantId, PlanId = plan.Id }
             join planCatalog in _dbContext.PlanCatalogs on new { subscription.TenantId, subscription.PlanId }
                 equals new { planCatalog.TenantId, planCatalog.PlanId }
             join catalog in _dbContext.Catalogs on new { planCatalog.TenantId, CatalogId = planCatalog.CatalogId }
                 equals new { catalog.TenantId, CatalogId = catalog.Id }
             join productCatalog in _dbContext.ProductCatalogs on new { planCatalog.TenantId, planCatalog.CatalogId }
                 equals new { productCatalog.TenantId, productCatalog.CatalogId }
             where subscription.TenantId == tenantId
                   && subscription.ClientId == clientId
                   && subscription.IsActive
                   && plan.IsActive
                   && catalog.IsActive
                   && subscription.StartsAt <= now
                   && subscription.EndsAt.HasValue
                   && now < subscription.EndsAt.Value
             select productCatalog.ProductSku).Distinct();
    }
}
