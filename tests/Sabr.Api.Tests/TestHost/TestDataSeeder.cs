using Microsoft.EntityFrameworkCore;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;
using Sabr.Domain.Protheus;

namespace Sabr.Api.Tests.TestHost;

public static class TestDataSeeder
{
    public static async Task SeedTenantAsync(
        DbContext dbContext,
        string tenantId,
        string slug,
        CancellationToken cancellationToken = default)
    {
        var db = (Sabr.Infrastructure.Persistence.AppDbContext)dbContext;
        if (await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
        {
            return;
        }

        db.Tenants.Add(new Sabr.Domain.Entities.Tenant
        {
            Id = tenantId,
            Name = $"Tenant {slug}",
            Slug = slug,
            Status = TenantStatus.Active
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task SeedClientCatalogGraphAsync(
        DbContext dbContext,
        string tenantId,
        Guid clientId,
        string allowedSku,
        string? blockedSku = null,
        CancellationToken cancellationToken = default)
    {
        var db = (Sabr.Infrastructure.Persistence.AppDbContext)dbContext;
        var now = DateTimeOffset.UtcNow;

        if (!await db.Clients.AnyAsync(c => c.Id == clientId, cancellationToken))
        {
            db.Clients.Add(new Client
            {
                Id = clientId,
                TenantId = tenantId,
                ProtheusCode = $"P-{clientId.ToString("N")[..6]}",
                AccountName = $"Client {clientId.ToString("N")[..6]}",
                Email = $"{clientId:N}@example.test",
                PasswordHash = "hash",
                Status = ClientStatus.Approved,
                MustChangePassword = false,
                ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.CREATE)
            });
        }

        var activePlanId = Guid.NewGuid();
        var inactivePlanId = Guid.NewGuid();
        var activeCatalogId = Guid.NewGuid();
        var inactiveCatalogId = Guid.NewGuid();

        if (!await db.Plans.AnyAsync(p => p.Id == activePlanId, cancellationToken))
        {
            db.Plans.Add(new Plan
            {
                Id = activePlanId,
                TenantId = tenantId,
                Name = $"PLAN-ACT-{activePlanId:N}",
                IsActive = true
            });
        }

        if (!await db.Plans.AnyAsync(p => p.Id == inactivePlanId, cancellationToken))
        {
            db.Plans.Add(new Plan
            {
                Id = inactivePlanId,
                TenantId = tenantId,
                Name = $"PLAN-INACT-{inactivePlanId:N}",
                IsActive = false
            });
        }

        if (!await db.Catalogs.AnyAsync(c => c.Id == activeCatalogId, cancellationToken))
        {
            db.Catalogs.Add(new Catalog
            {
                Id = activeCatalogId,
                TenantId = tenantId,
                Name = $"CAT-ACT-{activeCatalogId:N}",
                IsActive = true
            });
        }

        if (!await db.Catalogs.AnyAsync(c => c.Id == inactiveCatalogId, cancellationToken))
        {
            db.Catalogs.Add(new Catalog
            {
                Id = inactiveCatalogId,
                TenantId = tenantId,
                Name = $"CAT-INACT-{inactiveCatalogId:N}",
                IsActive = false
            });
        }

        if (!await db.PlanCatalogs.AnyAsync(pc => pc.TenantId == tenantId && pc.PlanId == activePlanId && pc.CatalogId == activeCatalogId, cancellationToken))
        {
            db.PlanCatalogs.Add(new PlanCatalog
            {
                TenantId = tenantId,
                PlanId = activePlanId,
                CatalogId = activeCatalogId
            });
        }

        if (!await db.PlanCatalogs.AnyAsync(pc => pc.TenantId == tenantId && pc.PlanId == inactivePlanId && pc.CatalogId == inactiveCatalogId, cancellationToken))
        {
            db.PlanCatalogs.Add(new PlanCatalog
            {
                TenantId = tenantId,
                PlanId = inactivePlanId,
                CatalogId = inactiveCatalogId
            });
        }

        if (!await db.Products.AnyAsync(p => p.Sku == allowedSku, cancellationToken))
        {
            db.Products.Add(new Product
            {
                Sku = allowedSku,
                Name = $"Allowed {allowedSku}",
                CostPriceCents = 1000,
                CatalogPriceCents = 1500,
                IsActive = true
            });
        }

        if (!await db.ProductCatalogs.AnyAsync(pc => pc.TenantId == tenantId && pc.CatalogId == activeCatalogId && pc.ProductSku == allowedSku, cancellationToken))
        {
            db.ProductCatalogs.Add(new ProductCatalog
            {
                TenantId = tenantId,
                CatalogId = activeCatalogId,
                ProductSku = allowedSku
            });
        }

        if (!string.IsNullOrWhiteSpace(blockedSku))
        {
            if (!await db.Products.AnyAsync(p => p.Sku == blockedSku, cancellationToken))
            {
                db.Products.Add(new Product
                {
                    Sku = blockedSku!,
                    Name = $"Blocked {blockedSku}",
                    CostPriceCents = 1200,
                    CatalogPriceCents = 1800,
                    IsActive = true
                });
            }

            if (!await db.ProductCatalogs.AnyAsync(pc => pc.TenantId == tenantId && pc.CatalogId == inactiveCatalogId && pc.ProductSku == blockedSku, cancellationToken))
            {
                db.ProductCatalogs.Add(new ProductCatalog
                {
                    TenantId = tenantId,
                    CatalogId = inactiveCatalogId,
                    ProductSku = blockedSku!
                });
            }
        }

        if (!await db.ClientPlanSubscriptions.AnyAsync(
                s => s.TenantId == tenantId && s.ClientId == clientId && s.PlanId == activePlanId,
                cancellationToken))
        {
            db.ClientPlanSubscriptions.Add(new ClientPlanSubscription
            {
                TenantId = tenantId,
                ClientId = clientId,
                PlanId = activePlanId,
                IsActive = true,
                StartsAt = now.AddDays(-1),
                EndsAt = now.AddDays(30)
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
