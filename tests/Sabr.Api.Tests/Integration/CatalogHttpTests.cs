using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Sabr.Api.Tests.TestHost;
using Sabr.Application.Models;
using Sabr.Infrastructure.Persistence;

namespace Sabr.Api.Tests.Integration;

public sealed class CatalogHttpTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public CatalogHttpTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Catalog_ListsOnlyAuthorizedProducts_AndSupportsCaseInsensitiveSearch()
    {
        const string tenantId = "tenant-a";
        const string slug = "sabr";
        var clientId = Guid.NewGuid();

        await _factory.ResetDatabaseAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await TestDataSeeder.SeedTenantAsync(db, tenantId, slug);
            await TestDataSeeder.SeedClientCatalogGraphAsync(db, tenantId, clientId, allowedSku: "SKU-777", blockedSku: "SKU-999");
        }

        using var client = _factory.CreateTenantClient(slug, tenantId, clientId);

        var listResponse = await client.GetAsync("/api/v1/catalog/products?skip=0&limit=20");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<PagedResult<CatalogProductDto>>();
        Assert.NotNull(list);
        Assert.Contains(list!.Items, item => item.Sku == "SKU-777");
        Assert.DoesNotContain(list.Items, item => item.Sku == "SKU-999");

        var searchBySku = await client.GetAsync("/api/v1/catalog/products?skip=0&limit=20&search=sku-777");
        Assert.Equal(HttpStatusCode.OK, searchBySku.StatusCode);
        var skuResult = await searchBySku.Content.ReadFromJsonAsync<PagedResult<CatalogProductDto>>();
        Assert.NotNull(skuResult);
        Assert.Single(skuResult!.Items);
        Assert.Equal("SKU-777", skuResult.Items[0].Sku);

        var searchByName = await client.GetAsync("/api/v1/catalog/products?skip=0&limit=20&search=allowed sku-777");
        Assert.Equal(HttpStatusCode.OK, searchByName.StatusCode);
        var nameResult = await searchByName.Content.ReadFromJsonAsync<PagedResult<CatalogProductDto>>();
        Assert.NotNull(nameResult);
        Assert.Single(nameResult!.Items);
        Assert.Equal("SKU-777", nameResult.Items[0].Sku);

        var maxLimit = await client.GetAsync("/api/v1/catalog/products?skip=0&limit=200");
        Assert.Equal(HttpStatusCode.OK, maxLimit.StatusCode);
        var maxLimitResult = await maxLimit.Content.ReadFromJsonAsync<PagedResult<CatalogProductDto>>();
        Assert.NotNull(maxLimitResult);
        Assert.True(maxLimitResult!.Items.Count >= 1);

        var aboveLimit = await client.GetAsync("/api/v1/catalog/products?skip=0&limit=201");
        Assert.Equal(HttpStatusCode.BadRequest, aboveLimit.StatusCode);
        var apiError = await aboveLimit.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(apiError);
        Assert.Equal("VALIDATION_ERROR", apiError!.Code);
        Assert.False(string.IsNullOrWhiteSpace(apiError.TraceId));
    }
}
