using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Services;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;
using Sabr.Infrastructure.Persistence;

namespace Sabr.Api.Tests;

public sealed class MyProductsServiceTests
{
    [Fact]
    public async Task AddToMyProducts_CreatesDraftWithSnapshots()
    {
        await using var db = CreateDb();
        var tenantId = "tenant-a";
        var clientId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var sku = "SKU-001";

        SeedAuthorizedSku(db, tenantId, clientId, sku);
        db.Products.Add(new Product
        {
            Sku = sku,
            Name = "Notebook",
            CostPriceCents = 10000,
            CatalogPriceCents = 15000,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.AddToMyProductsAsync(
            new AddMyProductRequest
            {
                ProductSku = sku,
                PricingMode = PricingMode.MarkupPercent,
                MarkupPercent = 10
            },
            tenantId,
            clientId,
            userId,
            idempotencyKey: null);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.True(result.Data!.Created);
        Assert.Equal(sku, result.Data.Draft.ProductSku);
        Assert.Equal(15000, result.Data.Draft.CatalogPriceCentsSnapshot);
        Assert.Equal(16500, result.Data.Draft.FinalPriceCentsSnapshot);

        var persisted = await db.Publications.SingleAsync();
        Assert.Equal(10000, persisted.CostPriceCentsSnapshot);
        Assert.Equal(15000, persisted.CatalogPriceCentsSnapshot);
        Assert.Equal(16500, persisted.FinalPriceCentsSnapshot);
        Assert.Equal(PublicationStatus.Draft, persisted.Status);
    }

    [Fact]
    public async Task AddToMyProducts_WithIdempotencyKey_ReturnsSameDraft()
    {
        await using var db = CreateDb();
        var tenantId = "tenant-a";
        var clientId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var sku = "SKU-002";
        SeedAuthorizedProduct(db, tenantId, clientId, sku, 20000, 25000);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var request = new AddMyProductRequest
        {
            ProductSku = sku,
            PricingMode = PricingMode.CatalogPrice
        };
        const string key = "my-products-idempotency-001";

        var first = await service.AddToMyProductsAsync(request, tenantId, clientId, userId, key);
        var second = await service.AddToMyProductsAsync(request, tenantId, clientId, userId, key);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.NotNull(first.Data);
        Assert.NotNull(second.Data);
        Assert.Equal(first.Data!.Draft.Id, second.Data!.Draft.Id);
        Assert.True(second.Data.FromIdempotencyCache);
        Assert.Equal(1, await db.Publications.CountAsync());
    }

    [Fact]
    public async Task AddToMyProducts_WithoutIdempotencyKey_DoesNotDuplicateDraft()
    {
        await using var db = CreateDb();
        var tenantId = "tenant-a";
        var clientId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var sku = "SKU-003";
        SeedAuthorizedProduct(db, tenantId, clientId, sku, 5000, 7000);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var request = new AddMyProductRequest { ProductSku = sku };

        var first = await service.AddToMyProductsAsync(request, tenantId, clientId, userId, null);
        var second = await service.AddToMyProductsAsync(request, tenantId, clientId, userId, null);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.NotNull(first.Data);
        Assert.NotNull(second.Data);
        Assert.True(first.Data!.Created);
        Assert.False(second.Data!.Created);
        Assert.Equal(first.Data.Draft.Id, second.Data.Draft.Id);
        Assert.Equal(1, await db.Publications.CountAsync());
    }

    [Fact]
    public async Task TenantIsolation_ListAndDelete_DoNotCrossTenant()
    {
        await using var db = CreateDb();
        var tenantA = "tenant-a";
        var clientA = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var tenantB = "tenant-b";
        var clientB = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var sku = "SKU-004";

        SeedAuthorizedProduct(db, tenantA, clientA, sku, 1000, 1500);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var add = await service.AddToMyProductsAsync(new AddMyProductRequest { ProductSku = sku }, tenantA, clientA, userA, null);
        Assert.True(add.Succeeded);
        var draftId = add.Data!.Draft.Id;

        var foreignList = await service.ListMyProductsAsync(tenantB, clientB, 0, 20, null);
        Assert.Empty(foreignList.Items);

        var removedByForeignTenant = await service.RemoveMyProductDraftAsync(draftId, tenantB, clientB, userB);
        Assert.False(removedByForeignTenant);

        var ownList = await service.ListMyProductsAsync(tenantA, clientA, 0, 20, null);
        Assert.Single(ownList.Items);
    }

    [Fact]
    public async Task UpdateMyProductDraft_RecalculatesFinalPrice_AndPreservesBaseSnapshots()
    {
        await using var db = CreateDb();
        var tenantId = "tenant-a";
        var clientId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var sku = "SKU-005";
        SeedAuthorizedProduct(db, tenantId, clientId, sku, 3000, 10000);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var add = await service.AddToMyProductsAsync(new AddMyProductRequest { ProductSku = sku }, tenantId, clientId, userId, null);
        Assert.True(add.Succeeded);
        var createdDraft = add.Data!.Draft;

        var update = await service.UpdateMyProductDraftAsync(
            createdDraft.Id,
            new UpdateMyProductDraftRequest
            {
                PricingMode = PricingMode.MarkupPercent,
                MarkupPercent = 25,
                RowVersion = createdDraft.RowVersion
            },
            tenantId,
            clientId,
            userId,
            ifMatch: null);

        Assert.True(update.Succeeded);
        Assert.NotNull(update.Data);
        Assert.Equal(12500, update.Data!.FinalPriceCentsSnapshot);
        Assert.Equal(10000, update.Data.CatalogPriceCentsSnapshot);

        var persisted = await db.Publications.SingleAsync();
        Assert.Equal(3000, persisted.CostPriceCentsSnapshot);
        Assert.Equal(10000, persisted.CatalogPriceCentsSnapshot);
        Assert.Equal(12500, persisted.FinalPriceCentsSnapshot);
    }

    [Fact]
    public async Task AddToMyProducts_SkuOutsideCatalog_ReturnsForbiddenError()
    {
        await using var db = CreateDb();
        var tenantId = "tenant-a";
        var clientId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.Products.Add(new Product
        {
            Sku = "SKU-NOT-ALLOWED",
            Name = "Blocked product",
            CostPriceCents = 1000,
            CatalogPriceCents = 1200,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.AddToMyProductsAsync(
            new AddMyProductRequest { ProductSku = "SKU-NOT-ALLOWED" },
            tenantId,
            clientId,
            userId,
            idempotencyKey: null);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Field == "productSku");
    }

    [Fact]
    public async Task UpdateMyProductDraft_WithMismatchedRowVersion_ReturnsConcurrencyError()
    {
        await using var db = CreateDb();
        var tenantId = "tenant-a";
        var clientId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var sku = "SKU-006";
        SeedAuthorizedProduct(db, tenantId, clientId, sku, 2200, 3300);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var add = await service.AddToMyProductsAsync(new AddMyProductRequest { ProductSku = sku }, tenantId, clientId, userId, null);
        Assert.True(add.Succeeded);

        var update = await service.UpdateMyProductDraftAsync(
            add.Data!.Draft.Id,
            new UpdateMyProductDraftRequest
            {
                PricingMode = PricingMode.FixedPrice,
                FixedPriceCents = 4000,
                RowVersion = "999999"
            },
            tenantId,
            clientId,
            userId,
            ifMatch: null);

        Assert.False(update.Succeeded);
        Assert.Contains(update.Errors, error => error.Field == "concurrency");
    }

    [Fact]
    public async Task ListMyProducts_SearchIsCaseInsensitiveForSkuAndProductName()
    {
        await using var db = CreateDb();
        var tenantId = "tenant-a";
        var clientId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        SeedAuthorizedProduct(db, tenantId, clientId, "SKU-ALFA-01", 1000, 1500);
        SeedAuthorizedProduct(db, tenantId, clientId, "SKU-BRAVO-02", 2000, 2800);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.AddToMyProductsAsync(new AddMyProductRequest { ProductSku = "SKU-ALFA-01" }, tenantId, clientId, userId, null);
        await service.AddToMyProductsAsync(new AddMyProductRequest { ProductSku = "SKU-BRAVO-02" }, tenantId, clientId, userId, null);

        var bySku = await service.ListMyProductsAsync(tenantId, clientId, 0, 20, "sku-alfa");
        var byName = await service.ListMyProductsAsync(tenantId, clientId, 0, 20, "bravo");

        Assert.Single(bySku.Items);
        Assert.Equal("SKU-ALFA-01", bySku.Items[0].ProductSku);

        Assert.Single(byName.Items);
        Assert.Equal("SKU-BRAVO-02", byName.Items[0].ProductSku);
    }

    [Fact]
    public async Task ListMyProducts_ReturnsVariantReadinessFlags()
    {
        await using var db = CreateDb();
        var tenantId = "tenant-a";
        var clientId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        SeedAuthorizedProduct(db, tenantId, clientId, "SKU-READY-01", 1000, 2000);
        SeedAuthorizedProduct(db, tenantId, clientId, "SKU-MISS-01", 1000, 2000);
        db.ProductVariants.Add(new ProductVariant
        {
            VariantSku = "SKU-READY-01",
            BaseSku = "SKU-READY-01",
            Name = "SKU-READY-01",
            CostPriceCents = 1000,
            CatalogPriceCents = 2000,
            PhysicalStock = 0,
            ReservedStock = 0,
            AvailableStock = 0,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.AddToMyProductsAsync(new AddMyProductRequest { ProductSku = "SKU-READY-01" }, tenantId, clientId, userId, null);
        await service.AddToMyProductsAsync(new AddMyProductRequest { ProductSku = "SKU-MISS-01" }, tenantId, clientId, userId, null);

        var result = await service.ListMyProductsAsync(tenantId, clientId, 0, 20, null);
        Assert.Equal(2, result.Items.Count);

        var ready = result.Items.Single(item => item.ProductSku == "SKU-READY-01");
        Assert.True(ready.HasProductVariant);
        Assert.Equal("Ready", ready.VariantStatus);

        var missing = result.Items.Single(item => item.ProductSku == "SKU-MISS-01");
        Assert.False(missing.HasProductVariant);
        Assert.Equal("Missing", missing.VariantStatus);
    }

    [Fact]
    public async Task ListMyProducts_ReturnsResolvedVariantStockFields()
    {
        await using var db = CreateDb();
        var tenantId = "tenant-a";
        var clientId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string baseSku = "SKU-BASE-RESOLVE-01";
        const string variantA = "SKU-BASE-RESOLVE-01-A";
        const string variantB = "SKU-BASE-RESOLVE-01-B";

        SeedAuthorizedProduct(db, tenantId, clientId, baseSku, 1000, 2000);
        db.ProductVariants.Add(new ProductVariant
        {
            VariantSku = variantA,
            BaseSku = baseSku,
            Name = variantA,
            CostPriceCents = 1000,
            CatalogPriceCents = 2000,
            PhysicalStock = 5,
            ReservedStock = 2,
            AvailableStock = 3,
            IsActive = true
        });
        db.ProductVariants.Add(new ProductVariant
        {
            VariantSku = variantB,
            BaseSku = baseSku,
            Name = variantB,
            CostPriceCents = 1000,
            CatalogPriceCents = 2000,
            PhysicalStock = 9,
            ReservedStock = 1,
            AvailableStock = 8,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.AddToMyProductsAsync(new AddMyProductRequest { ProductSku = baseSku }, tenantId, clientId, userId, null);

        var result = await service.ListMyProductsAsync(tenantId, clientId, 0, 20, null);

        Assert.Single(result.Items);
        var row = result.Items[0];
        Assert.Equal(variantB, row.ResolvedVariantSku);
        Assert.Equal(8, row.AvailableStock);
        Assert.Equal("AutoBestVariant", row.StockSource);
    }

    [Fact]
    public async Task ProductVariantBackfillService_CreatesMissingVariant_AndIsIdempotent()
    {
        await using var db = CreateDb();
        const string tenantId = "tenant-backfill";
        var clientId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string sku = "SKU-BACKFILL-01";

        SeedAuthorizedProduct(db, tenantId, clientId, sku, 1200, 2200);
        await db.SaveChangesAsync();

        var myProductsService = CreateService(db);
        var add = await myProductsService.AddToMyProductsAsync(
            new AddMyProductRequest { ProductSku = sku },
            tenantId,
            clientId,
            userId,
            null);
        Assert.True(add.Succeeded);

        var backfill = new ProductVariantBackfillService(db, NullLogger<ProductVariantBackfillService>.Instance);

        var firstRun = await backfill.RunOnceAsync(200);
        Assert.Equal(1, firstRun.Processed);
        Assert.Equal(1, firstRun.Created);
        Assert.Equal(0, firstRun.Errors);
        Assert.Equal(0, firstRun.SkippedMissingProduct);
        Assert.Equal(0, firstRun.AlreadyExists);

        var createdVariant = await db.ProductVariants.SingleAsync(item => item.VariantSku == sku);
        Assert.Equal(sku, createdVariant.BaseSku);
        Assert.Equal(0, createdVariant.PhysicalStock);
        Assert.Equal(0, createdVariant.ReservedStock);
        Assert.Equal(0, createdVariant.AvailableStock);

        var secondRun = await backfill.RunOnceAsync(200);
        Assert.Equal(0, secondRun.Processed);
        Assert.Equal(0, secondRun.Created);
        Assert.Equal(1, await db.ProductVariants.CountAsync(item => item.VariantSku == sku));
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new AppDbContext(options);
    }

    private static MyProductsService CreateService(AppDbContext dbContext)
    {
        return new MyProductsService(
            dbContext,
            new CatalogAuthorizationService(dbContext),
            new PriceCalculator());
    }

    private static void SeedAuthorizedProduct(
        AppDbContext dbContext,
        string tenantId,
        Guid clientId,
        string sku,
        long costPriceCents,
        long catalogPriceCents)
    {
        SeedAuthorizedSku(dbContext, tenantId, clientId, sku);
        dbContext.Products.Add(new Product
        {
            Sku = sku,
            Name = $"Product {sku}",
            CostPriceCents = costPriceCents,
            CatalogPriceCents = catalogPriceCents,
            IsActive = true
        });
    }

    private static void SeedAuthorizedSku(AppDbContext dbContext, string tenantId, Guid clientId, string sku)
    {
        var planId = Guid.NewGuid();
        var catalogId = Guid.NewGuid();

        dbContext.Plans.Add(new Plan
        {
            Id = planId,
            TenantId = tenantId,
            Name = $"Plan {planId:N}",
            IsActive = true
        });

        dbContext.Catalogs.Add(new Catalog
        {
            Id = catalogId,
            TenantId = tenantId,
            Name = $"Catalog {catalogId:N}",
            IsActive = true
        });

        dbContext.PlanCatalogs.Add(new PlanCatalog
        {
            TenantId = tenantId,
            PlanId = planId,
            CatalogId = catalogId
        });

        dbContext.ProductCatalogs.Add(new ProductCatalog
        {
            TenantId = tenantId,
            CatalogId = catalogId,
            ProductSku = sku
        });

        dbContext.ClientPlanSubscriptions.Add(new ClientPlanSubscription
        {
            TenantId = tenantId,
            ClientId = clientId,
            PlanId = planId,
            IsActive = true,
            StartsAt = DateTimeOffset.UtcNow.AddDays(-1),
            EndsAt = DateTimeOffset.UtcNow.AddDays(30)
        });
    }
}
