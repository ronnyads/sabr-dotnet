using Microsoft.EntityFrameworkCore;
using Phub.Application.Security;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Domain.Protheus;
using Phub.Domain.ValueObjects;

namespace Phub.Infrastructure.Persistence.Seeding;

public sealed class StagingFakeSeeder
{
    private const string DefaultTenantId = "stg_sabr";
    private const string TenantSlug = "sabr";
    private static readonly Guid UncategorizedCategoryId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ActivePlanId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid InactivePlanId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ActiveCatalogId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid InactiveCatalogId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private readonly AppDbContext _dbContext;

    public StagingFakeSeeder(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        var tenant = await GetOrCreateTenantAsync(now, cancellationToken);
        var tenantId = tenant.Id;
        var client = await GetOrCreateClientAsync(tenantId, now, cancellationToken);

        await EnsureDefaultCategoryAsync(now, cancellationToken);
        await SeedProductsAsync(now, cancellationToken);
        await SeedPlansAndCatalogsAsync(tenantId, now, cancellationToken);
        await SeedRelationsAsync(tenantId, now, cancellationToken);
        await SeedSubscriptionsAsync(tenantId, client.Id, now, cancellationToken);
    }

    private async Task EnsureDefaultCategoryAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var category = await _dbContext.Categories.FirstOrDefaultAsync(item => item.Slug == ProductAdminService.UncategorizedSlug, cancellationToken);
        if (category != null)
        {
            category.Name = "Sem Categoria";
            category.IsActive = true;
            category.UpdatedAt = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        _dbContext.Categories.Add(new Category
        {
            Id = UncategorizedCategoryId,
            Name = "Sem Categoria",
            Slug = ProductAdminService.UncategorizedSlug,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Tenant> GetOrCreateTenantAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenant = await _dbContext.Tenants.FirstOrDefaultAsync(item => item.Slug == TenantSlug, cancellationToken);
        if (tenant != null)
        {
            tenant.Name = "SABR STAGING";
            tenant.Status = TenantStatus.Active;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return tenant;
        }

        tenant = await _dbContext.Tenants.FirstOrDefaultAsync(item => item.Id == DefaultTenantId, cancellationToken);
        if (tenant != null)
        {
            tenant.Name = "SABR STAGING";
            tenant.Slug = TenantSlug;
            tenant.Status = TenantStatus.Active;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return tenant;
        }

        tenant = new Tenant
        {
            Id = DefaultTenantId,
            Name = "SABR STAGING",
            Slug = TenantSlug,
            Status = TenantStatus.Active,
            CreatedAt = now
        };

        _dbContext.Tenants.Add(tenant);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return tenant;
    }

    private async Task<Client> GetOrCreateClientAsync(string tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var fakePasswordHash = PasswordHasher.HashPassword("Fake1234!");
        var client = await _dbContext.Clients.FirstOrDefaultAsync(
            item => item.TenantId == tenantId && item.Email == "cliente.sabr.staging@example.test",
            cancellationToken);

        if (client != null)
        {
            client.Status = ClientStatus.Approved;
            client.MustChangePassword = false;
            client.PasswordHash = fakePasswordHash;
            client.AccountName = "SABR CLIENTE STAGING";
            client.LegalName = "SABR CLIENTE STAGING LTDA";
            client.TradeName = "SABR STAGING";
            client.City = "Sao Paulo";
            client.State = "SP";
            await _dbContext.SaveChangesAsync(cancellationToken);
            return client;
        }

        client = new Client
        {
            TenantId = tenantId,
            ProtheusCode = "990100",
            AccountName = "SABR CLIENTE STAGING",
            Email = "cliente.sabr.staging@example.test",
            PasswordHash = fakePasswordHash,
            LegalName = "SABR CLIENTE STAGING LTDA",
            TradeName = "SABR STAGING",
            Document = "00000000000100",
            ResponsibleName = "USUARIO STAGING SABR",
            ResponsibleDocument = "00000000000",
            City = "Sao Paulo",
            State = "SP",
            Status = ClientStatus.Approved,
            MustChangePassword = false,
            ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.CREATE),
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.Clients.Add(client);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return client;
    }

    private async Task SeedProductsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var products = new[]
        {
            new ProductSeed("STG-SKU-001", "Camiseta Dryfit Basica", 2590, 3990),
            new ProductSeed("STG-SKU-002", "Mochila Executiva 20L", 8990, 12990),
            new ProductSeed("STG/SKU-003", "Mouse Sem Fio Pro", 3590, 5490),
            new ProductSeed("STG_SKU_004", "Headset Office", 7990, 11490),
            new ProductSeed("STG-SKU-005", "Teclado Slim", 4990, 7490)
        };

        foreach (var item in products)
        {
            var sku = Sku.Normalize(item.Sku);
            var product = await _dbContext.Products.FirstOrDefaultAsync(p => p.Sku == sku, cancellationToken);
            if (product == null)
            {
                _dbContext.Products.Add(new Product
                {
                    Sku = sku,
                    Name = item.Name,
                    Brand = "PHUB",
                    CategoryId = ProductAdminService.UncategorizedSlug,
                    CostPriceCents = item.CostPriceCents,
                    CatalogPriceCents = item.CatalogPriceCents,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                continue;
            }

            product.Name = item.Name;
            product.Brand = string.IsNullOrWhiteSpace(product.Brand) ? "PHUB" : product.Brand;
            product.CategoryId = string.IsNullOrWhiteSpace(product.CategoryId) ? ProductAdminService.UncategorizedSlug : product.CategoryId;
            product.CostPriceCents = item.CostPriceCents;
            product.CatalogPriceCents = item.CatalogPriceCents;
            product.IsActive = true;
            product.UpdatedAt = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedPlansAndCatalogsAsync(string tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await UpsertPlanAsync(tenantId, ActivePlanId, "Plano Staging Base", isActive: true, now, cancellationToken);
        await UpsertPlanAsync(tenantId, InactivePlanId, "Plano Staging Inativo", isActive: false, now, cancellationToken);

        await UpsertCatalogAsync(tenantId, ActiveCatalogId, "Catalogo Staging", "Catalogo ativo para homologacao", true, now, cancellationToken);
        await UpsertCatalogAsync(tenantId, InactiveCatalogId, "Catalogo Staging Inativo", "Catalogo inativo para validacao", false, now, cancellationToken);
    }

    private async Task SeedRelationsAsync(string tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await UpsertPlanCatalogAsync(tenantId, ActivePlanId, ActiveCatalogId, now, cancellationToken);
        await UpsertPlanCatalogAsync(tenantId, InactivePlanId, InactiveCatalogId, now, cancellationToken);

        var activeSkus = new[] { "STG-SKU-001", "STG-SKU-002", "STG/SKU-003", "STG_SKU_004", "STG-SKU-005" };
        foreach (var sku in activeSkus)
        {
            await UpsertProductCatalogAsync(tenantId, ActiveCatalogId, sku, now, cancellationToken);
        }

        await UpsertProductCatalogAsync(tenantId, InactiveCatalogId, "STG-SKU-005", now, cancellationToken);
    }

    private async Task SeedSubscriptionsAsync(string tenantId, Guid clientId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var activeSubscription = await _dbContext.ClientPlanSubscriptions.FirstOrDefaultAsync(
            item => item.TenantId == tenantId && item.ClientId == clientId && item.PlanId == ActivePlanId,
            cancellationToken);

        if (activeSubscription == null)
        {
            _dbContext.ClientPlanSubscriptions.Add(new ClientPlanSubscription
            {
                TenantId = tenantId,
                ClientId = clientId,
                PlanId = ActivePlanId,
                IsActive = true,
                StartsAt = now.AddDays(-30),
                EndsAt = now.AddYears(1),
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            activeSubscription.IsActive = true;
            activeSubscription.StartsAt = now.AddDays(-30);
            activeSubscription.EndsAt = now.AddYears(1);
            activeSubscription.UpdatedAt = now;
        }

        var inactiveSubscription = await _dbContext.ClientPlanSubscriptions.FirstOrDefaultAsync(
            item => item.TenantId == tenantId && item.ClientId == clientId && item.PlanId == InactivePlanId,
            cancellationToken);

        if (inactiveSubscription == null)
        {
            _dbContext.ClientPlanSubscriptions.Add(new ClientPlanSubscription
            {
                TenantId = tenantId,
                ClientId = clientId,
                PlanId = InactivePlanId,
                IsActive = false,
                StartsAt = now.AddDays(-90),
                EndsAt = now.AddDays(-30),
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            inactiveSubscription.IsActive = false;
            inactiveSubscription.StartsAt = now.AddDays(-90);
            inactiveSubscription.EndsAt = now.AddDays(-30);
            inactiveSubscription.UpdatedAt = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertPlanAsync(string tenantId, Guid id, string name, bool isActive, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var plan = await _dbContext.Plans.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (plan == null)
        {
            _dbContext.Plans.Add(new Plan
            {
                Id = id,
                Name = name,
                IsActive = isActive,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            plan.Name = name;
            plan.IsActive = isActive;
            plan.UpdatedAt = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertCatalogAsync(
        string tenantId,
        Guid id,
        string name,
        string description,
        bool isActive,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var catalog = await _dbContext.Catalogs.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (catalog == null)
        {
            _dbContext.Catalogs.Add(new Catalog
            {
                Id = id,
                Name = name,
                Description = description,
                IsActive = isActive,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            catalog.Name = name;
            catalog.Description = description;
            catalog.IsActive = isActive;
            catalog.UpdatedAt = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertPlanCatalogAsync(string tenantId, Guid planId, Guid catalogId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var relation = await _dbContext.PlanCatalogs.FirstOrDefaultAsync(
            item => item.PlanId == planId && item.CatalogId == catalogId,
            cancellationToken);

        if (relation != null)
        {
            return;
        }

        _dbContext.PlanCatalogs.Add(new PlanCatalog
        {
            PlanId = planId,
            CatalogId = catalogId,
            CreatedAt = now
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertProductCatalogAsync(string tenantId, Guid catalogId, string sku, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var normalizedSku = Sku.Normalize(sku);
        var relation = await _dbContext.ProductCatalogs.FirstOrDefaultAsync(
            item => item.CatalogId == catalogId && item.ProductSku == normalizedSku,
            cancellationToken);

        if (relation != null)
        {
            return;
        }

        _dbContext.ProductCatalogs.Add(new ProductCatalog
        {
            CatalogId = catalogId,
            ProductSku = normalizedSku,
            CreatedAt = now
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private sealed record ProductSeed(string Sku, string Name, long CostPriceCents, long CatalogPriceCents);
}
