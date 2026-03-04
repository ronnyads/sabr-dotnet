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

public sealed class AdminPlansHttpTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AdminPlansHttpTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AdminPlans_CrudAndReplaceCatalogs_AreTenantScopedAndIdempotent()
    {
        const string tenantA = "tenant-a";
        const string tenantB = "tenant-b";
        const string slugA = "sabr";
        const string slugB = "orion";

        Guid catalogAId;
        Guid catalogBId;

        await _factory.ResetDatabaseAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            await TestDataSeeder.SeedTenantAsync(db, tenantA, slugA);
            await TestDataSeeder.SeedTenantAsync(db, tenantB, slugB);

            catalogAId = Guid.NewGuid();
            catalogBId = Guid.NewGuid();

            db.Catalogs.AddRange(
                new Catalog
                {
                    Id = catalogAId,
                    TenantId = tenantA,
                    Name = "Catalogo A",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                new Catalog
                {
                    Id = catalogBId,
                    TenantId = tenantB,
                    Name = "Catalogo B",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });

            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAdminClient();

        var createResponse = await client.PostAsJsonAsync($"/api/v1/admin/tenants/{slugA}/plans", new AdminPlanUpsertRequest
        {
            Name = "Plano Premium",
            IsActive = true
        });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<AdminPlanDetailResult>();
        Assert.NotNull(created);

        var replaceCatalogs = await client.PutAsJsonAsync(
            $"/api/v1/admin/tenants/{slugA}/plans/{created!.Id}/catalogs",
            new PlanReplaceCatalogsRequest
            {
                CatalogIds = new List<Guid> { catalogAId, catalogAId }
            });

        Assert.Equal(HttpStatusCode.OK, replaceCatalogs.StatusCode);
        var linked = await replaceCatalogs.Content.ReadFromJsonAsync<AdminPlanDetailResult>();
        Assert.NotNull(linked);
        Assert.Single(linked!.CatalogIds);
        Assert.Equal(catalogAId, linked.CatalogIds[0]);

        var replaceAgain = await client.PutAsJsonAsync(
            $"/api/v1/admin/tenants/{slugA}/plans/{created.Id}/catalogs",
            new PlanReplaceCatalogsRequest
            {
                CatalogIds = new List<Guid> { catalogAId }
            });

        Assert.Equal(HttpStatusCode.OK, replaceAgain.StatusCode);
        var linkedAgain = await replaceAgain.Content.ReadFromJsonAsync<AdminPlanDetailResult>();
        Assert.NotNull(linkedAgain);
        Assert.Single(linkedAgain!.CatalogIds);

        var crossTenant = await client.PutAsJsonAsync(
            $"/api/v1/admin/tenants/{slugA}/plans/{created.Id}/catalogs",
            new PlanReplaceCatalogsRequest
            {
                CatalogIds = new List<Guid> { catalogBId }
            });

        Assert.Equal((HttpStatusCode)422, crossTenant.StatusCode);
        var crossTenantError = await crossTenant.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(crossTenantError);
        Assert.Equal("INVALID_CATALOG_IDS", crossTenantError!.Code);
        var invalidCatalogIds = ReadStringArray(crossTenantError.Errors, "invalidCatalogIds");
        Assert.Single(invalidCatalogIds);
        Assert.Equal(catalogBId.ToString(), invalidCatalogIds[0], ignoreCase: true);

        var listResponse = await client.GetAsync($"/api/v1/admin/tenants/{slugA}/plans?skip=0&limit=20&search=premium");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<PagedResult<AdminPlanResult>>();
        Assert.NotNull(list);
        Assert.Contains(list!.Items, item => item.Id == created.Id);

        var deleteFirst = await client.DeleteAsync($"/api/v1/admin/tenants/{slugA}/plans/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteFirst.StatusCode);

        var deleteSecond = await client.DeleteAsync($"/api/v1/admin/tenants/{slugA}/plans/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteSecond.StatusCode);
    }

    [Fact]
    public async Task AdminPlans_WhenTenantIsInactive_ReturnsTenantInactive()
    {
        const string tenantId = "tenant-inactive";
        const string slug = "inactive-plan";

        await _factory.ResetDatabaseAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Tenants.Add(new Sabr.Domain.Entities.Tenant
            {
                Id = tenantId,
                Name = "Tenant Inativo Planos",
                Slug = slug,
                Status = TenantStatus.Inactive
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAdminClient();
        var response = await client.GetAsync($"/api/v1/admin/tenants/{slug}/plans?skip=0&limit=20");

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
