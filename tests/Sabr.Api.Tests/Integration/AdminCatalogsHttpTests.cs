using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sabr.Api.Tests.TestHost;
using Sabr.Application.Models;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;
using Sabr.Infrastructure.Persistence;

namespace Sabr.Api.Tests.Integration;

public sealed class AdminCatalogsHttpTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AdminCatalogsHttpTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AdminCatalogs_CrudAndReplaceSet_AreTenantScopedAndIdempotent()
    {
        const string tenantA = "tenant-a";
        const string tenantB = "tenant-b";
        const string slugA = "sabr";
        const string slugB = "orion";

        Guid planAId;
        Guid planBId;

        await _factory.ResetDatabaseAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            await TestDataSeeder.SeedTenantAsync(db, tenantA, slugA);
            await TestDataSeeder.SeedTenantAsync(db, tenantB, slugB);

            planAId = Guid.NewGuid();
            planBId = Guid.NewGuid();

            db.Plans.AddRange(
                new Plan
                {
                    Id = planAId,
                    TenantId = tenantA,
                    Name = "Plano A",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                new Plan
                {
                    Id = planBId,
                    TenantId = tenantB,
                    Name = "Plano B",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });

            db.Products.AddRange(
                new Product
                {
                    Sku = "sku-100",
                    Name = "Produto 100",
                    CostPriceCents = 100,
                    CatalogPriceCents = 200,
                    IsActive = true
                },
                new Product
                {
                    Sku = "sku-200",
                    Name = "Produto 200",
                    CostPriceCents = 120,
                    CatalogPriceCents = 260,
                    IsActive = true
                });

            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAdminClient();

        var createResponse = await client.PostAsJsonAsync($"/api/v1/admin/tenants/{slugA}/catalogs", new AdminCatalogUpsertRequest
        {
            Name = "Catalogo Principal",
            Description = "Catalogo base do tenant",
            IsActive = true
        });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<AdminCatalogDetailResult>();
        Assert.NotNull(created);

        var replaceProducts = await client.PutAsJsonAsync(
            $"/api/v1/admin/tenants/{slugA}/catalogs/{created!.Id}/products",
            new CatalogReplaceProductsRequest
            {
                ProductSkus = new List<string> { "sku-100", "SKU-100", "SKU-200" }
            });

        Assert.Equal(HttpStatusCode.OK, replaceProducts.StatusCode);
        var productsLinked = await replaceProducts.Content.ReadFromJsonAsync<AdminCatalogDetailResult>();
        Assert.NotNull(productsLinked);
        Assert.Equal(2, productsLinked!.ProductSkus.Count);
        Assert.Contains("SKU-100", productsLinked.ProductSkus);
        Assert.Contains("SKU-200", productsLinked.ProductSkus);

        var invalidProducts = await client.PutAsJsonAsync(
            $"/api/v1/admin/tenants/{slugA}/catalogs/{created.Id}/products",
            new CatalogReplaceProductsRequest
            {
                ProductSkus = new List<string> { "sku invalido" }
            });

        Assert.Equal((HttpStatusCode)422, invalidProducts.StatusCode);
        var invalidProductError = await invalidProducts.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(invalidProductError);
        Assert.Equal("INVALID_PRODUCT_SKU", invalidProductError!.Code);
        var invalidSkuList = ReadStringArray(invalidProductError.Errors, "invalidSkus");
        Assert.Single(invalidSkuList);
        Assert.Equal("SKU INVALIDO", invalidSkuList[0]);

        var invalidPlans = await client.PutAsJsonAsync(
            $"/api/v1/admin/tenants/{slugA}/catalogs/{created.Id}/plans",
            new CatalogReplacePlansRequest
            {
                PlanIds = new List<Guid> { planBId }
            });

        Assert.Equal((HttpStatusCode)422, invalidPlans.StatusCode);
        var invalidPlanError = await invalidPlans.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(invalidPlanError);
        Assert.Equal("INVALID_PLAN_IDS", invalidPlanError!.Code);
        var invalidPlanIds = ReadStringArray(invalidPlanError.Errors, "invalidPlanIds");
        Assert.Single(invalidPlanIds);
        Assert.Equal(planBId.ToString(), invalidPlanIds[0], ignoreCase: true);

        var replacePlans = await client.PutAsJsonAsync(
            $"/api/v1/admin/tenants/{slugA}/catalogs/{created.Id}/plans",
            new CatalogReplacePlansRequest
            {
                PlanIds = new List<Guid> { planAId, planAId }
            });

        Assert.Equal(HttpStatusCode.OK, replacePlans.StatusCode);
        var plansLinked = await replacePlans.Content.ReadFromJsonAsync<AdminCatalogDetailResult>();
        Assert.NotNull(plansLinked);
        Assert.Single(plansLinked!.PlanIds);
        Assert.Equal(planAId, plansLinked.PlanIds[0]);

        var listResponse = await client.GetAsync($"/api/v1/admin/tenants/{slugA}/catalogs?skip=0&limit=20&search=principal");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<PagedResult<AdminCatalogResult>>();
        Assert.NotNull(list);
        Assert.Contains(list!.Items, item => item.Id == created.Id);

        var deleteFirst = await client.DeleteAsync($"/api/v1/admin/tenants/{slugA}/catalogs/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteFirst.StatusCode);

        var deleteSecond = await client.DeleteAsync($"/api/v1/admin/tenants/{slugA}/catalogs/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteSecond.StatusCode);
    }

    [Fact]
    public async Task AdminCatalogs_WhenTenantSlugDoesNotExist_ReturnsNotFound()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateAdminClient();

        var response = await client.GetAsync("/api/v1/admin/tenants/missing-slug/catalogs?skip=0&limit=20");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal("TENANT_NOT_FOUND", error!.Code);
    }

    [Fact]
    public async Task AdminCatalogs_WhenTenantIsInactive_ReturnsTenantInactive()
    {
        const string tenantId = "tenant-inactive";
        const string slug = "inactive";

        await _factory.ResetDatabaseAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Tenants.Add(new Sabr.Domain.Entities.Tenant
            {
                Id = tenantId,
                Name = "Tenant Inativo",
                Slug = slug,
                Status = TenantStatus.Inactive
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAdminClient();
        var response = await client.GetAsync($"/api/v1/admin/tenants/{slug}/catalogs?skip=0&limit=20");

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal("TENANT_INACTIVE", error!.Code);
    }

    private static List<string> ReadStringArray(object? errors, string propertyName)
    {
        if (errors is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            return new List<string>();
        }

        if (!element.TryGetProperty(propertyName, out var arrayElement) || arrayElement.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        return arrayElement
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }
}
