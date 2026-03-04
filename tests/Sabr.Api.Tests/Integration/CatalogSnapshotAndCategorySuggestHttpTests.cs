using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sabr.Api.Tests.TestHost;
using Sabr.Application.Models;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;
using Sabr.Domain.Protheus;
using Sabr.Infrastructure.Persistence;

namespace Sabr.Api.Tests.Integration;

public sealed class CatalogSnapshotHttpTests : IClassFixture<MercadoLivreTestWebApplicationFactory>
{
    private readonly MercadoLivreTestWebApplicationFactory _factory;

    public CatalogSnapshotHttpTests(MercadoLivreTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CatalogSnapshot_ReturnsData_FromProductVariantProductAndImages()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-snapshot-01";
        const string tenantSlug = "tenantsnapshot01";
        var clientId = Guid.NewGuid();
        const string sku = "SKU-SNAPSHOT-01";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductWithVariantAsync(sku, includeVariant: true, includeImage: true);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/catalog/variants/snapshot",
            new CatalogVariantSnapshotRequest
            {
                VariantSku = sku
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<CatalogVariantSnapshotResult>();
        Assert.NotNull(payload);
        Assert.Equal(sku, payload!.VariantSku);
        Assert.Equal(sku, payload.BaseSku);
        Assert.Equal("BRL", payload.CurrencyId);
        Assert.Equal(33m, payload.CatalogPrice);
        Assert.True(payload.Images.Count > 0);
        Assert.Equal("gold_special", payload.ListingTypeDefault);
        Assert.Equal("MLB", payload.SiteId);
        Assert.Equal(sku, payload.ResolvedVariantSku);
        Assert.Equal("ExactVariant", payload.StockSource);
        Assert.False(payload.VariantBackfilled);
    }

    [Fact]
    public async Task CatalogSnapshot_WhenRequestIsBaseSkuWithActiveVariants_UsesAutoBestVariantWithoutBackfill()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-snapshot-05";
        const string tenantSlug = "tenantsnapshot05";
        var clientId = Guid.NewGuid();
        const string baseSku = "SKU-SNAPSHOT-BASE-05";
        const string variantA = "SKU-SNAPSHOT-BASE-05-A";
        const string variantB = "SKU-SNAPSHOT-BASE-05-B";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Products.Add(new Product
            {
                Sku = baseSku,
                Name = $"Produto {baseSku}",
                Brand = "Marca Snapshot",
                Ncm = "33049990",
                Ean = "7891234567890",
                Description = "Descricao de teste snapshot",
                CostPriceCents = 2500,
                CatalogPriceCents = 3300,
                IsActive = true
            });
            db.ProductVariants.Add(new ProductVariant
            {
                VariantSku = variantA,
                BaseSku = baseSku,
                Name = $"Variante {variantA}",
                CostPriceCents = 2500,
                CatalogPriceCents = 3300,
                PhysicalStock = 4,
                ReservedStock = 2,
                AvailableStock = 2,
                IsActive = true
            });
            db.ProductVariants.Add(new ProductVariant
            {
                VariantSku = variantB,
                BaseSku = baseSku,
                Name = $"Variante {variantB}",
                CostPriceCents = 2500,
                CatalogPriceCents = 3300,
                PhysicalStock = 10,
                ReservedStock = 1,
                AvailableStock = 9,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/catalog/variants/snapshot",
            new CatalogVariantSnapshotRequest
            {
                VariantSku = baseSku
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<CatalogVariantSnapshotResult>();
        Assert.NotNull(payload);
        Assert.Equal(9, payload!.StockAvailable);
        Assert.Equal(33m, payload.CatalogPrice);
        Assert.Equal(variantB, payload.ResolvedVariantSku);
        Assert.Equal("AutoBestVariant", payload.StockSource);
        Assert.False(payload.VariantBackfilled);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await verifyDb.ProductVariants.AnyAsync(item => item.VariantSku == baseSku));
    }

    [Fact]
    public async Task CatalogSnapshot_AutoBackfillsVariant_WhenVariantMissingAndProductExists()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-snapshot-02";
        const string tenantSlug = "tenantsnapshot02";
        var clientId = Guid.NewGuid();
        const string sku = "SKU-SNAPSHOT-02";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductWithVariantAsync(sku, includeVariant: false, includeImage: false);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/catalog/variants/snapshot",
            new CatalogVariantSnapshotRequest
            {
                VariantSku = sku
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<CatalogVariantSnapshotResult>();
        Assert.NotNull(payload);
        Assert.True(payload!.VariantBackfilled);
        Assert.Equal(0, payload.StockAvailable);
        Assert.Equal(33m, payload.CatalogPrice);
        Assert.Equal(sku, payload.ResolvedVariantSku);
        Assert.Equal("ExactVariant", payload.StockSource);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.ProductVariants.AnyAsync(item => item.VariantSku == sku));
    }

    [Fact]
    public async Task CatalogSnapshot_Backfill_AllowsDraftGet_AfterSnapshot()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-snapshot-04";
        const string tenantSlug = "tenantsnapshot04";
        var clientId = Guid.NewGuid();
        const string sku = "SAD151412";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductWithVariantAsync(sku, includeVariant: false, includeImage: true);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var snapshotResponse = await client.PostAsJsonAsync(
            "/api/v1/client/catalog/variants/snapshot",
            new CatalogVariantSnapshotRequest
            {
                VariantSku = sku
            });
        Assert.Equal(HttpStatusCode.OK, snapshotResponse.StatusCode);

        var draftGetResponse = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/get",
            new ListingDraftGetRequest
            {
                VariantSku = sku,
                Channel = "mercadolivre"
            });

        Assert.Equal(HttpStatusCode.OK, draftGetResponse.StatusCode);
        var payload = await draftGetResponse.Content.ReadFromJsonAsync<ListingDraftGetResult>();
        Assert.NotNull(payload);
        Assert.Contains(payload!.Candidates, item => string.Equals(item.SabrVariantSku, sku, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CatalogSnapshot_WhenResolvedVariantHasNoCatalogPrice_FallsBackToBaseProductCatalogPrice()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-snapshot-06";
        const string tenantSlug = "tenantsnapshot06";
        var clientId = Guid.NewGuid();
        const string baseSku = "SKU-SNAPSHOT-BASE-06";
        const string variantA = "SKU-SNAPSHOT-BASE-06-A";
        const string variantB = "SKU-SNAPSHOT-BASE-06-B";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Products.Add(new Product
            {
                Sku = baseSku,
                Name = $"Produto {baseSku}",
                Brand = "Marca Snapshot",
                Ncm = "33049990",
                Ean = "7891234567890",
                Description = "Descricao de teste snapshot",
                CostPriceCents = 2500,
                CatalogPriceCents = 4500,
                IsActive = true
            });
            db.ProductVariants.Add(new ProductVariant
            {
                VariantSku = variantA,
                BaseSku = baseSku,
                Name = $"Variante {variantA}",
                CostPriceCents = 2500,
                CatalogPriceCents = 0,
                PhysicalStock = 4,
                ReservedStock = 2,
                AvailableStock = 2,
                IsActive = true
            });
            db.ProductVariants.Add(new ProductVariant
            {
                VariantSku = variantB,
                BaseSku = baseSku,
                Name = $"Variante {variantB}",
                CostPriceCents = 2500,
                CatalogPriceCents = 0,
                PhysicalStock = 10,
                ReservedStock = 1,
                AvailableStock = 9,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/catalog/variants/snapshot",
            new CatalogVariantSnapshotRequest
            {
                VariantSku = baseSku
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<CatalogVariantSnapshotResult>();
        Assert.NotNull(payload);
        Assert.Equal(45m, payload!.CatalogPrice);
    }

    [Fact]
    public async Task CatalogSnapshot_WhenCatalogPriceUnavailable_ReturnsNullCatalogPrice()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-snapshot-07";
        const string tenantSlug = "tenantsnapshot07";
        var clientId = Guid.NewGuid();
        const string sku = "SKU-SNAPSHOT-07";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Products.Add(new Product
            {
                Sku = sku,
                Name = $"Produto {sku}",
                Brand = "Marca Snapshot",
                Ncm = "33049990",
                Ean = "7891234567890",
                Description = "Descricao de teste snapshot",
                CostPriceCents = 2500,
                CatalogPriceCents = 0,
                IsActive = true
            });
            db.ProductVariants.Add(new ProductVariant
            {
                VariantSku = sku,
                BaseSku = sku,
                Name = $"Variante {sku}",
                CostPriceCents = 2500,
                CatalogPriceCents = 0,
                PhysicalStock = 10,
                ReservedStock = 1,
                AvailableStock = 9,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/catalog/variants/snapshot",
            new CatalogVariantSnapshotRequest
            {
                VariantSku = sku
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<CatalogVariantSnapshotResult>();
        Assert.NotNull(payload);
        Assert.Null(payload!.CatalogPrice);
    }

    [Fact]
    public async Task CatalogSnapshot_Returns422ProductNotFound_WhenSkuUnknown()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-snapshot-03";
        const string tenantSlug = "tenantsnapshot03";
        var clientId = Guid.NewGuid();

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/catalog/variants/snapshot",
            new CatalogVariantSnapshotRequest
            {
                VariantSku = "SKU-SNAPSHOT-UNKNOWN-03"
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("PRODUCT_NOT_FOUND", payload!.Code);
    }

    private async Task SeedTenantClientAsync(string tenantId, string tenantSlug, Guid clientId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await db.Tenants.AnyAsync(item => item.Id == tenantId))
        {
            db.Tenants.Add(new Sabr.Domain.Entities.Tenant
            {
                Id = tenantId,
                Name = $"Tenant {tenantSlug}",
                Slug = tenantSlug,
                Status = TenantStatus.Active
            });
        }

        if (!await db.Clients.AnyAsync(item => item.Id == clientId))
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

        await db.SaveChangesAsync();
    }

    private async Task SeedProductWithVariantAsync(string sku, bool includeVariant, bool includeImage)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await db.Products.AnyAsync(item => item.Sku == sku))
        {
            db.Products.Add(new Product
            {
                Sku = sku,
                Name = $"Produto {sku}",
                Brand = "Marca Snapshot",
                Ncm = "33049990",
                Ean = "7891234567890",
                Description = "Descricao de teste snapshot",
                CostPriceCents = 2500,
                CatalogPriceCents = 3300,
                IsActive = true
            });
        }

        if (includeVariant && !await db.ProductVariants.AnyAsync(item => item.VariantSku == sku))
        {
            db.ProductVariants.Add(new ProductVariant
            {
                VariantSku = sku,
                BaseSku = sku,
                Name = $"Variante {sku}",
                CostPriceCents = 2500,
                CatalogPriceCents = 3300,
                PhysicalStock = 10,
                ReservedStock = 1,
                AvailableStock = 9,
                IsActive = true
            });
        }

        if (includeImage && !await db.ProductImages.AnyAsync(item => item.ProductSku == sku))
        {
            db.ProductImages.Add(new ProductImage
            {
                Id = Guid.NewGuid(),
                ProductSku = sku,
                Url = $"https://images.example.test/{sku}.jpg",
                MimeType = "image/jpeg",
                SizeBytes = 1024,
                SortOrder = 0,
                IsPrimary = true
            });
        }

        await db.SaveChangesAsync();
    }

}
