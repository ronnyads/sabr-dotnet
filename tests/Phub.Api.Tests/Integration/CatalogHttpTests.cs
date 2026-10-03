using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Phub.Api.Tests.TestHost;
using Phub.Application.Models;
using Phub.Domain.Entities;
using Phub.Infrastructure.Persistence;

namespace Phub.Api.Tests.Integration;

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

            var authorizedCatalogId = db.ProductCatalogs.Single(item => item.ProductSku == "SKU-777").CatalogId;
            db.Products.Add(new Product
            {
                Sku = "MLB123456789",
                Name = "Marketplace listing that is not an internal product",
                CatalogPriceCents = 9999,
                CostPriceCents = 9999,
                IsActive = true
            });
            db.ProductCatalogs.Add(new ProductCatalog
            {
                CatalogId = authorizedCatalogId,
                ProductSku = "MLB123456789"
            });
            db.ProductVariants.Add(new ProductVariant
            {
                BaseSku = "SKU-777",
                VariantSku = "SKU-777-RED",
                Name = "Red",
                AvailableStock = 12,
                PhysicalStock = 12,
                SafetyBuffer = 0,
                CatalogPriceCents = 1500,
                CostPriceCents = 1000,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateTenantClient(slug, tenantId, clientId);

        var listResponse = await client.GetAsync("/api/v1/catalog/products?skip=0&limit=20");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<CatalogProductPageDto>();
        Assert.NotNull(list);
        Assert.Contains(list!.Items, item => item.Sku == "SKU-777");
        Assert.DoesNotContain(list.Items, item => item.Sku == "SKU-999");
        Assert.DoesNotContain(list.Items, item => item.Sku.StartsWith("MLB", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, list.Total);
        Assert.Equal(1, list.Facets.InStockCount);

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

        var detailResponse = await client.GetAsync("/api/v1/catalog/products/SKU-777");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await detailResponse.Content.ReadFromJsonAsync<CatalogProductDetailDto>();
        Assert.NotNull(detail);
        Assert.Equal("SKU-777", detail!.Sku);
        Assert.Equal(12, detail.AvailableStock);
        Assert.Single(detail.Variants);

        var forbiddenDetail = await client.GetAsync("/api/v1/catalog/products/SKU-999");
        Assert.Equal(HttpStatusCode.NotFound, forbiddenDetail.StatusCode);

        var rawMarketplaceDetail = await client.GetAsync("/api/v1/catalog/products/MLB123456789");
        Assert.Equal(HttpStatusCode.NotFound, rawMarketplaceDetail.StatusCode);
    }

    [Fact]
    public async Task ProductCorrectionRequest_RequiresCatalogAuthorization_AndAuditsAuthorizedRequest()
    {
        const string tenantId = "tenant-correction";
        const string slug = "tenantcorrection";
        var clientId = Guid.NewGuid();

        await _factory.ResetDatabaseAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await TestDataSeeder.SeedTenantAsync(db, tenantId, slug);
            await TestDataSeeder.SeedClientCatalogGraphAsync(
                db,
                tenantId,
                clientId,
                allowedSku: "SKU-CORRECTION-ALLOWED",
                blockedSku: "SKU-CORRECTION-BLOCKED");
        }

        using var client = _factory.CreateTenantClient(slug, tenantId, clientId);
        var blockedResponse = await client.PostAsJsonAsync(
            "/api/v1/client/publications/product-correction-requests",
            new ProductCorrectionRequest
            {
                ProductId = "SKU-CORRECTION-BLOCKED",
                Fields = new List<string> { "NCM" },
                Message = "Corrigir NCM."
            });
        Assert.Equal(HttpStatusCode.NotFound, blockedResponse.StatusCode);

        var acceptedResponse = await client.PostAsJsonAsync(
            "/api/v1/client/publications/product-correction-requests",
            new ProductCorrectionRequest
            {
                ProductId = " sku-correction-allowed ",
                Fields = new List<string> { "ncm", "NCM", "gtin" },
                Message = " Corrigir dados fiscais. "
            });
        Assert.Equal(HttpStatusCode.Accepted, acceptedResponse.StatusCode);
        var accepted = await acceptedResponse.Content.ReadFromJsonAsync<ProductCorrectionRequestResult>();
        Assert.NotNull(accepted);
        Assert.NotEqual(Guid.Empty, accepted!.RequestId);
        Assert.Equal("OPEN", accepted.Status);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await verifyDb.AuditEvents.AsNoTracking().SingleAsync(item =>
            item.TenantId == tenantId &&
            item.RequestId == accepted.RequestId &&
            item.Action == "ProductCorrection.Requested");
        Assert.Contains("SKU-CORRECTION-ALLOWED", audit.MetadataJson);
        Assert.Contains("Corrigir dados fiscais.", audit.MetadataJson);
    }
}
