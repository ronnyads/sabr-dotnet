using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sabr.Api.Tests.TestHost;
using Sabr.Application.Models;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;
using Sabr.Domain.Protheus;
using Sabr.Infrastructure.Persistence;

namespace Sabr.Api.Tests.Integration;

public sealed class AdminClientPlanSubscriptionsHttpTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AdminClientPlanSubscriptionsHttpTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ReplaceSet_WhenClientWithoutSubscription_ThenCatalogBecomesVisible_AndCanBeRemoved()
    {
        const string tenantId = "tenant-a";
        const string slug = "sabr";
        var clientId = Guid.NewGuid();

        var planId = Guid.NewGuid();

        await _factory.ResetDatabaseAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await SeedGraphAsync(db, tenantId, slug, clientId);

            var activeCatalogId = await db.Catalogs
                .Where(item => item.TenantId == tenantId && item.IsActive)
                .Select(item => item.Id)
                .FirstAsync();

            db.Plans.Add(new Plan
            {
                Id = planId,
                TenantId = tenantId,
                Name = "Plano Mensal Cliente",
                BillingPeriod = BillingPeriod.Monthly,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            db.PlanCatalogs.Add(new PlanCatalog
            {
                TenantId = tenantId,
                PlanId = planId,
                CatalogId = activeCatalogId,
                CreatedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync();
        }

        using var adminClient = _factory.CreateAdminClient();
        using var tenantClient = _factory.CreateTenantClient(slug, tenantId, clientId);

        var emptyCatalogResponse = await tenantClient.GetAsync("/api/v1/catalog/products?skip=0&limit=20");
        Assert.Equal(HttpStatusCode.OK, emptyCatalogResponse.StatusCode);
        var emptyCatalog = await emptyCatalogResponse.Content.ReadFromJsonAsync<PagedResult<CatalogProductDto>>();
        Assert.NotNull(emptyCatalog);
        Assert.Empty(emptyCatalog!.Items);

        var putResponse = await adminClient.PutAsJsonAsync(
            $"/api/v1/admin/tenants/{slug}/clients/{clientId}/plan-subscriptions",
            new ClientPlanSubscriptionsReplaceRequest { PlanIds = new List<Guid> { planId } });

        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);
        var updated = await putResponse.Content.ReadFromJsonAsync<ClientPlanSubscriptionsResult>();
        Assert.NotNull(updated);
        Assert.Single(updated!.Items);
        Assert.Equal(planId, updated.Items[0].PlanId);
        Assert.Equal("Monthly", updated.Items[0].BillingPeriod.ToString());

        var catalogResponse = await tenantClient.GetAsync("/api/v1/catalog/products?skip=0&limit=20");
        Assert.Equal(HttpStatusCode.OK, catalogResponse.StatusCode);
        var catalog = await catalogResponse.Content.ReadFromJsonAsync<PagedResult<CatalogProductDto>>();
        Assert.NotNull(catalog);
        Assert.Contains(catalog!.Items, item => item.Sku == "SKU-777");

        var clearResponse = await adminClient.PutAsJsonAsync(
            $"/api/v1/admin/tenants/{slug}/clients/{clientId}/plan-subscriptions",
            new ClientPlanSubscriptionsReplaceRequest { PlanIds = new List<Guid>() });

        Assert.Equal(HttpStatusCode.OK, clearResponse.StatusCode);
        var cleared = await clearResponse.Content.ReadFromJsonAsync<ClientPlanSubscriptionsResult>();
        Assert.NotNull(cleared);
        Assert.Empty(cleared!.Items);

        var catalogAfterClearResponse = await tenantClient.GetAsync("/api/v1/catalog/products?skip=0&limit=20");
        Assert.Equal(HttpStatusCode.OK, catalogAfterClearResponse.StatusCode);
        var catalogAfterClear = await catalogAfterClearResponse.Content.ReadFromJsonAsync<PagedResult<CatalogProductDto>>();
        Assert.NotNull(catalogAfterClear);
        Assert.Empty(catalogAfterClear!.Items);
    }

    [Fact]
    public async Task ReplaceSet_WhenPlanBelongsToOtherTenant_ReturnsInvalidPlanIds()
    {
        const string tenantA = "tenant-a";
        const string slugA = "sabr";
        const string tenantB = "tenant-b";
        const string slugB = "orion";

        var clientId = Guid.NewGuid();
        var otherTenantPlanId = Guid.NewGuid();

        await _factory.ResetDatabaseAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await SeedGraphAsync(db, tenantA, slugA, clientId);
            await TestDataSeeder.SeedTenantAsync(db, tenantB, slugB);

            db.Plans.Add(new Plan
            {
                Id = otherTenantPlanId,
                TenantId = tenantB,
                Name = "Plano Outro Tenant",
                BillingPeriod = BillingPeriod.Monthly,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync();
        }

        using var adminClient = _factory.CreateAdminClient();
        var response = await adminClient.PutAsJsonAsync(
            $"/api/v1/admin/tenants/{slugA}/clients/{clientId}/plan-subscriptions",
            new ClientPlanSubscriptionsReplaceRequest { PlanIds = new List<Guid> { otherTenantPlanId } });

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal("INVALID_PLAN_IDS", error!.Code);
        var invalidIds = ReadStringArray(error.Errors, "invalidPlanIds");
        Assert.Single(invalidIds);
        Assert.Equal(otherTenantPlanId.ToString(), invalidIds[0], ignoreCase: true);
    }

    [Fact]
    public async Task ReplaceSet_WhenPlanIsInactive_ReturnsPlanInactiveWithInactivePlanIds()
    {
        const string tenantId = "tenant-a";
        const string slug = "sabr";
        var clientId = Guid.NewGuid();
        var inactivePlanId = Guid.NewGuid();

        await _factory.ResetDatabaseAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await SeedGraphAsync(db, tenantId, slug, clientId);

            db.Plans.Add(new Plan
            {
                Id = inactivePlanId,
                TenantId = tenantId,
                Name = "Plano Inativo",
                BillingPeriod = BillingPeriod.Monthly,
                IsActive = false,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync();
        }

        using var adminClient = _factory.CreateAdminClient();
        var response = await adminClient.PutAsJsonAsync(
            $"/api/v1/admin/tenants/{slug}/clients/{clientId}/plan-subscriptions",
            new ClientPlanSubscriptionsReplaceRequest { PlanIds = new List<Guid> { inactivePlanId } });

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal("PLAN_INACTIVE", error!.Code);
        var inactiveIds = ReadStringArray(error.Errors, "inactivePlanIds");
        Assert.Single(inactiveIds);
        Assert.Equal(inactivePlanId.ToString(), inactiveIds[0], ignoreCase: true);
    }

    [Fact]
    public async Task ReplaceSet_WhenClientNotInTenant_ReturnsClientNotFound()
    {
        const string tenantId = "tenant-a";
        const string slug = "sabr";
        var foreignClientId = Guid.NewGuid();

        await _factory.ResetDatabaseAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await TestDataSeeder.SeedTenantAsync(db, tenantId, slug);
        }

        using var adminClient = _factory.CreateAdminClient();
        var response = await adminClient.PutAsJsonAsync(
            $"/api/v1/admin/tenants/{slug}/clients/{foreignClientId}/plan-subscriptions",
            new ClientPlanSubscriptionsReplaceRequest { PlanIds = new List<Guid>() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal("CLIENT_NOT_FOUND", error!.Code);
    }

    [Fact]
    public async Task ReplaceSet_IsIdempotent_AndDoesNotRenewValidWindow()
    {
        const string tenantId = "tenant-a";
        const string slug = "sabr";
        var clientId = Guid.NewGuid();
        var planId = Guid.NewGuid();

        await _factory.ResetDatabaseAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await SeedGraphAsync(db, tenantId, slug, clientId);
            var activeCatalogId = await db.Catalogs
                .Where(item => item.TenantId == tenantId && item.IsActive)
                .Select(item => item.Id)
                .FirstAsync();

            db.Plans.Add(new Plan
            {
                Id = planId,
                TenantId = tenantId,
                Name = "Plano Estavel",
                BillingPeriod = BillingPeriod.Monthly,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            db.PlanCatalogs.Add(new PlanCatalog
            {
                TenantId = tenantId,
                PlanId = planId,
                CatalogId = activeCatalogId,
                CreatedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync();
        }

        using var adminClient = _factory.CreateAdminClient();

        var firstResponse = await adminClient.PutAsJsonAsync(
            $"/api/v1/admin/tenants/{slug}/clients/{clientId}/plan-subscriptions",
            new ClientPlanSubscriptionsReplaceRequest { PlanIds = new List<Guid> { planId } });

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<ClientPlanSubscriptionsResult>();
        Assert.NotNull(first);
        Assert.Single(first!.Items);

        await Task.Delay(25);

        var secondResponse = await adminClient.PutAsJsonAsync(
            $"/api/v1/admin/tenants/{slug}/clients/{clientId}/plan-subscriptions",
            new ClientPlanSubscriptionsReplaceRequest { PlanIds = new List<Guid> { planId } });

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var second = await secondResponse.Content.ReadFromJsonAsync<ClientPlanSubscriptionsResult>();
        Assert.NotNull(second);
        Assert.Single(second!.Items);

        Assert.Equal(first.Items[0].StartsAt, second.Items[0].StartsAt);
        Assert.Equal(first.Items[0].EndsAt, second.Items[0].EndsAt);

        using var verifyScope = _factory.Services.CreateScope();
        var dbContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var activeCount = await dbContext.ClientPlanSubscriptions.CountAsync(item =>
            item.TenantId == tenantId &&
            item.ClientId == clientId &&
            item.PlanId == planId &&
            item.IsActive);
        Assert.Equal(1, activeCount);
    }

    [Fact]
    public async Task ReplaceSet_ComputesEndsAtByBillingPeriod_AndGetIsDeterministic()
    {
        const string tenantId = "tenant-a";
        const string slug = "sabr";
        var clientId = Guid.NewGuid();

        var planMonthly = Guid.NewGuid();
        var planQuarterly = Guid.NewGuid();
        var planSemiannual = Guid.NewGuid();
        var planAnnual = Guid.NewGuid();

        await _factory.ResetDatabaseAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await SeedGraphAsync(db, tenantId, slug, clientId);
            var activeCatalogId = await db.Catalogs
                .Where(item => item.TenantId == tenantId && item.IsActive)
                .Select(item => item.Id)
                .FirstAsync();

            db.Plans.AddRange(
                new Plan
                {
                    Id = planMonthly,
                    TenantId = tenantId,
                    Name = "Plano Mensal",
                    BillingPeriod = BillingPeriod.Monthly,
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                new Plan
                {
                    Id = planQuarterly,
                    TenantId = tenantId,
                    Name = "Plano Trimestral",
                    BillingPeriod = BillingPeriod.Quarterly,
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                new Plan
                {
                    Id = planSemiannual,
                    TenantId = tenantId,
                    Name = "Plano Semestral",
                    BillingPeriod = BillingPeriod.Semiannual,
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                new Plan
                {
                    Id = planAnnual,
                    TenantId = tenantId,
                    Name = "Plano Anual",
                    BillingPeriod = BillingPeriod.Annual,
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });

            db.PlanCatalogs.AddRange(
                new PlanCatalog { TenantId = tenantId, PlanId = planMonthly, CatalogId = activeCatalogId, CreatedAt = DateTimeOffset.UtcNow },
                new PlanCatalog { TenantId = tenantId, PlanId = planQuarterly, CatalogId = activeCatalogId, CreatedAt = DateTimeOffset.UtcNow },
                new PlanCatalog { TenantId = tenantId, PlanId = planSemiannual, CatalogId = activeCatalogId, CreatedAt = DateTimeOffset.UtcNow },
                new PlanCatalog { TenantId = tenantId, PlanId = planAnnual, CatalogId = activeCatalogId, CreatedAt = DateTimeOffset.UtcNow });

            await db.SaveChangesAsync();
        }

        using var adminClient = _factory.CreateAdminClient();
        var putResponse = await adminClient.PutAsJsonAsync(
            $"/api/v1/admin/tenants/{slug}/clients/{clientId}/plan-subscriptions",
            new ClientPlanSubscriptionsReplaceRequest
            {
                PlanIds = new List<Guid> { planAnnual, planMonthly, planQuarterly, planSemiannual }
            });

        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);
        var putResult = await putResponse.Content.ReadFromJsonAsync<ClientPlanSubscriptionsResult>();
        Assert.NotNull(putResult);
        Assert.Equal(4, putResult!.Items.Count);

        Assert.Equal(planMonthly, putResult.Items[0].PlanId);
        Assert.Equal(planQuarterly, putResult.Items[1].PlanId);
        Assert.Equal(planSemiannual, putResult.Items[2].PlanId);
        Assert.Equal(planAnnual, putResult.Items[3].PlanId);

        AssertEndsAtMonths(putResult.Items.Single(item => item.PlanId == planMonthly), 1);
        AssertEndsAtMonths(putResult.Items.Single(item => item.PlanId == planQuarterly), 3);
        AssertEndsAtMonths(putResult.Items.Single(item => item.PlanId == planSemiannual), 6);
        AssertEndsAtMonths(putResult.Items.Single(item => item.PlanId == planAnnual), 12);

        var getResponse = await adminClient.GetAsync(
            $"/api/v1/admin/tenants/{slug}/clients/{clientId}/plan-subscriptions");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getResult = await getResponse.Content.ReadFromJsonAsync<ClientPlanSubscriptionsResult>();
        Assert.NotNull(getResult);
        Assert.Equal(4, getResult!.Items.Count);
        Assert.Equal(putResult.Items.Select(item => item.PlanId), getResult.Items.Select(item => item.PlanId));
    }

    private static async Task SeedGraphAsync(
        AppDbContext db,
        string tenantId,
        string tenantSlug,
        Guid clientId)
    {
        await TestDataSeeder.SeedTenantAsync(db, tenantId, tenantSlug);
        await TestDataSeeder.SeedClientCatalogGraphAsync(db, tenantId, clientId, allowedSku: "SKU-777", blockedSku: "SKU-999");

        var activeSubscriptions = await db.ClientPlanSubscriptions
            .Where(item => item.TenantId == tenantId && item.ClientId == clientId && item.IsActive)
            .ToListAsync();

        foreach (var item in activeSubscriptions)
        {
            item.IsActive = false;
            item.EndsAt = DateTimeOffset.UtcNow;
            item.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    private static void AssertEndsAtMonths(ClientPlanSubscriptionItemResult item, int months)
    {
        var expected = item.StartsAt.AddMonths(months);
        var deltaSeconds = Math.Abs((expected - item.EndsAt).TotalSeconds);
        Assert.True(deltaSeconds < 1, $"Expected endsAt {expected:o}, actual {item.EndsAt:o}, delta {deltaSeconds}s");
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
