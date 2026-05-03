using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Phub.Api.Tests.TestHost;
using Phub.Application.Models;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Infrastructure.Persistence;

namespace Phub.Api.Tests.Integration;

public sealed class MyProductsHttpTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public MyProductsHttpTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Post_CreateAndRepeat_Returns201Then200WithSameDraft()
    {
        const string tenantId = "tenant-a";
        const string slug = "sabr";
        var clientId = Guid.NewGuid();

        await _factory.ResetDatabaseAsync();
        await SeedAsync(tenantId, slug, clientId, allowedSku: "SKU-001");
        using var client = _factory.CreateTenantClient(slug, tenantId, clientId);

        var first = await client.PostAsJsonAsync("/api/v1/my-products", new AddMyProductRequest
        {
            ProductSku = "sku-001"
        });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.NotNull(first.Headers.ETag);
        var firstEtag = first.Headers.ETag!.Tag;
        var created = await first.Content.ReadFromJsonAsync<MyProductDraftResult>();
        Assert.NotNull(created);
        Assert.Equal("SKU-001", created!.ProductSku);

        var repeat = await client.PostAsJsonAsync("/api/v1/my-products", new AddMyProductRequest
        {
            ProductSku = "SKU-001"
        });

        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.NotNull(repeat.Headers.ETag);
        var existing = await repeat.Content.ReadFromJsonAsync<MyProductDraftResult>();
        Assert.NotNull(existing);
        Assert.Equal(created.Id, existing!.Id);
        Assert.Equal(firstEtag, repeat.Headers.ETag!.Tag);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var draftCount = await db.Publications
            .CountAsync(p =>
                p.TenantId == tenantId &&
                p.ClientId == clientId &&
                p.ProductSku == "SKU-001" &&
                p.Status == PublicationStatus.Draft);
        Assert.Equal(1, draftCount);
    }

    [Fact]
    public async Task Put_WithInvalidIfMatch_Returns409ConcurrencyConflict()
    {
        const string tenantId = "tenant-a";
        const string slug = "sabr";
        var clientId = Guid.NewGuid();

        await _factory.ResetDatabaseAsync();
        await SeedAsync(tenantId, slug, clientId, allowedSku: "SKU-002");
        using var client = _factory.CreateTenantClient(slug, tenantId, clientId);

        var post = await client.PostAsJsonAsync("/api/v1/my-products", new AddMyProductRequest
        {
            ProductSku = "SKU-002"
        });
        var created = await post.Content.ReadFromJsonAsync<MyProductDraftResult>();
        Assert.NotNull(created);

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/my-products/{created!.Id}")
        {
            Content = JsonContent.Create(new UpdateMyProductDraftRequest
            {
                PricingMode = PricingMode.FixedPrice,
                FixedPriceCents = 2300
            })
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"999999\"");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Correlation-Id"));

        var apiError = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(apiError);
        Assert.Equal("CONCURRENCY_CONFLICT", apiError!.Code);
        Assert.False(string.IsNullOrWhiteSpace(apiError.TraceId));
    }

    [Fact]
    public async Task Put_WithoutIfMatchAndRowVersion_Returns428PreconditionRequired()
    {
        const string tenantId = "tenant-a";
        const string slug = "sabr";
        var clientId = Guid.NewGuid();

        await _factory.ResetDatabaseAsync();
        await SeedAsync(tenantId, slug, clientId, allowedSku: "SKU-022");
        using var client = _factory.CreateTenantClient(slug, tenantId, clientId);

        var post = await client.PostAsJsonAsync("/api/v1/my-products", new AddMyProductRequest
        {
            ProductSku = "SKU-022"
        });
        var created = await post.Content.ReadFromJsonAsync<MyProductDraftResult>();
        Assert.NotNull(created);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/my-products/{created!.Id}",
            new UpdateMyProductDraftRequest
            {
                PricingMode = PricingMode.FixedPrice,
                FixedPriceCents = 2800
            });

        Assert.Equal((HttpStatusCode)428, response.StatusCode);
        var apiError = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(apiError);
        Assert.Equal("PRECONDITION_REQUIRED", apiError!.Code);
        Assert.False(string.IsNullOrWhiteSpace(apiError.TraceId));
    }

    [Fact]
    public async Task Delete_IsIdempotentAndReturns204Always()
    {
        const string tenantId = "tenant-a";
        const string slug = "sabr";
        var clientId = Guid.NewGuid();

        await _factory.ResetDatabaseAsync();
        await SeedAsync(tenantId, slug, clientId, allowedSku: "SKU-003");
        using var client = _factory.CreateTenantClient(slug, tenantId, clientId);

        var post = await client.PostAsJsonAsync("/api/v1/my-products", new AddMyProductRequest
        {
            ProductSku = "SKU-003"
        });
        var created = await post.Content.ReadFromJsonAsync<MyProductDraftResult>();
        Assert.NotNull(created);

        var firstDelete = await client.DeleteAsync($"/api/v1/my-products/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, firstDelete.StatusCode);

        var secondDelete = await client.DeleteAsync($"/api/v1/my-products/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, secondDelete.StatusCode);
    }

    [Fact]
    public async Task GetAndPut_ReturnEtagAndRowVersion()
    {
        const string tenantId = "tenant-a";
        const string slug = "sabr";
        var clientId = Guid.NewGuid();

        await _factory.ResetDatabaseAsync();
        await SeedAsync(tenantId, slug, clientId, allowedSku: "SKU-004");
        using var client = _factory.CreateTenantClient(slug, tenantId, clientId);

        var post = await client.PostAsJsonAsync("/api/v1/my-products", new AddMyProductRequest
        {
            ProductSku = "SKU-004"
        });
        var created = await post.Content.ReadFromJsonAsync<MyProductDraftResult>();
        Assert.NotNull(created);
        Assert.NotNull(post.Headers.ETag);
        Assert.False(string.IsNullOrWhiteSpace(created!.RowVersion));

        var get = await client.GetAsync($"/api/v1/my-products/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.NotNull(get.Headers.ETag);
        var getEtag = get.Headers.ETag!.Tag;
        var current = await get.Content.ReadFromJsonAsync<MyProductDraftResult>();
        Assert.NotNull(current);
        Assert.False(string.IsNullOrWhiteSpace(current!.RowVersion));
        var beforeRowVersion = current.RowVersion;

        using var putReq = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/my-products/{created.Id}")
        {
            Content = JsonContent.Create(new UpdateMyProductDraftRequest
            {
                PricingMode = PricingMode.MarkupPercent,
                MarkupPercent = 10,
                RowVersion = current.RowVersion
            })
        };
        putReq.Headers.TryAddWithoutValidation("If-Match", $"\"{current.RowVersion}\"");
        var put = await client.SendAsync(putReq);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.NotNull(put.Headers.ETag);
        var putEtag = put.Headers.ETag!.Tag;
        var updated = await put.Content.ReadFromJsonAsync<MyProductDraftResult>();
        Assert.NotNull(updated);
        Assert.False(string.IsNullOrWhiteSpace(updated!.RowVersion));
        Assert.NotEqual(beforeRowVersion, updated.RowVersion);
        Assert.NotEqual(getEtag, putEtag);
    }

    [Fact]
    public async Task GetAndPut_WhenDraftDoesNotExist_Return404DraftNotFound()
    {
        const string tenantId = "tenant-a";
        const string slug = "sabr";
        var clientId = Guid.NewGuid();
        var draftId = Guid.NewGuid();

        await _factory.ResetDatabaseAsync();
        await SeedAsync(tenantId, slug, clientId, allowedSku: "SKU-044");
        using var client = _factory.CreateTenantClient(slug, tenantId, clientId);

        var getResponse = await client.GetAsync($"/api/v1/my-products/{draftId}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
        var getError = await getResponse.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(getError);
        Assert.Equal("DRAFT_NOT_FOUND", getError!.Code);
        Assert.False(string.IsNullOrWhiteSpace(getError.TraceId));

        using var putRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/my-products/{draftId}")
        {
            Content = JsonContent.Create(new UpdateMyProductDraftRequest
            {
                PricingMode = PricingMode.CatalogPrice,
                RowVersion = "1"
            })
        };
        putRequest.Headers.TryAddWithoutValidation("If-Match", "\"1\"");

        var putResponse = await client.SendAsync(putRequest);
        Assert.Equal(HttpStatusCode.NotFound, putResponse.StatusCode);
        var putError = await putResponse.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(putError);
        Assert.Equal("DRAFT_NOT_FOUND", putError!.Code);
        Assert.False(string.IsNullOrWhiteSpace(putError.TraceId));
    }

    [Fact]
    public async Task List_SearchIsCaseInsensitive_ForSkuAndProductName()
    {
        const string tenantId = "tenant-a";
        const string slug = "sabr";
        var clientId = Guid.NewGuid();

        await _factory.ResetDatabaseAsync();
        await SeedAsync(tenantId, slug, clientId, allowedSku: "SKU-777");
        await SeedAsync(tenantId, slug, clientId, allowedSku: "SKU-888");
        using var client = _factory.CreateTenantClient(slug, tenantId, clientId);

        await client.PostAsJsonAsync("/api/v1/my-products", new AddMyProductRequest { ProductSku = "SKU-777" });
        await client.PostAsJsonAsync("/api/v1/my-products", new AddMyProductRequest { ProductSku = "SKU-888" });

        var searchBySku = await client.GetAsync("/api/v1/my-products?skip=0&limit=20&search=sku-777");
        Assert.Equal(HttpStatusCode.OK, searchBySku.StatusCode);
        var skuResult = await searchBySku.Content.ReadFromJsonAsync<PagedResult<MyProductDraftResult>>();
        Assert.NotNull(skuResult);
        Assert.Single(skuResult!.Items);
        Assert.Equal("SKU-777", skuResult.Items[0].ProductSku);

        var searchByName = await client.GetAsync("/api/v1/my-products?skip=0&limit=20&search=allowed sku-888");
        Assert.Equal(HttpStatusCode.OK, searchByName.StatusCode);
        var nameResult = await searchByName.Content.ReadFromJsonAsync<PagedResult<MyProductDraftResult>>();
        Assert.NotNull(nameResult);
        Assert.Single(nameResult!.Items);
        Assert.Equal("SKU-888", nameResult.Items[0].ProductSku);
    }

    [Fact]
    public async Task List_ReturnsVariantReadinessFlags()
    {
        const string tenantId = "tenant-a";
        const string slug = "sabr";
        var clientId = Guid.NewGuid();

        await _factory.ResetDatabaseAsync();
        await SeedAsync(tenantId, slug, clientId, allowedSku: "SKU-READY-HTTP");
        await SeedAsync(tenantId, slug, clientId, allowedSku: "SKU-MISS-HTTP");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ProductVariants.Add(new ProductVariant
            {
                VariantSku = "SKU-READY-HTTP",
                BaseSku = "SKU-READY-HTTP",
                Name = "SKU-READY-HTTP",
                CostPriceCents = 1000,
                CatalogPriceCents = 1200,
                PhysicalStock = 0,
                ReservedStock = 0,
                AvailableStock = 0,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateTenantClient(slug, tenantId, clientId);
        await client.PostAsJsonAsync("/api/v1/my-products", new AddMyProductRequest { ProductSku = "SKU-READY-HTTP" });
        await client.PostAsJsonAsync("/api/v1/my-products", new AddMyProductRequest { ProductSku = "SKU-MISS-HTTP" });

        var response = await client.GetAsync("/api/v1/my-products?skip=0&limit=20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<PagedResult<MyProductDraftResult>>();
        Assert.NotNull(payload);
        Assert.Equal(2, payload!.Items.Count);

        var ready = payload.Items.Single(item => item.ProductSku == "SKU-READY-HTTP");
        Assert.True(ready.HasProductVariant);
        Assert.Equal("Ready", ready.VariantStatus);

        var missing = payload.Items.Single(item => item.ProductSku == "SKU-MISS-HTTP");
        Assert.False(missing.HasProductVariant);
        Assert.Equal("Missing", missing.VariantStatus);
    }

    [Fact]
    public async Task List_WhenBaseSkuHasVariants_ReturnsResolvedVariantStockFields()
    {
        const string tenantId = "tenant-a";
        const string slug = "sabr";
        var clientId = Guid.NewGuid();
        const string baseSku = "SKU-BASE-LIST-RESOLVE-01";
        const string variantA = "SKU-BASE-LIST-RESOLVE-01-A";
        const string variantB = "SKU-BASE-LIST-RESOLVE-01-B";

        await _factory.ResetDatabaseAsync();
        await SeedAsync(tenantId, slug, clientId, allowedSku: baseSku);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await db.ProductVariants.AnyAsync(item => item.VariantSku == variantA))
            {
                db.ProductVariants.Add(new ProductVariant
                {
                    VariantSku = variantA,
                    BaseSku = baseSku,
                    Name = variantA,
                    CostPriceCents = 1000,
                    CatalogPriceCents = 1200,
                    PhysicalStock = 3,
                    ReservedStock = 1,
                    AvailableStock = 2,
                    IsActive = true
                });
            }

            if (!await db.ProductVariants.AnyAsync(item => item.VariantSku == variantB))
            {
                db.ProductVariants.Add(new ProductVariant
                {
                    VariantSku = variantB,
                    BaseSku = baseSku,
                    Name = variantB,
                    CostPriceCents = 1000,
                    CatalogPriceCents = 1200,
                    PhysicalStock = 10,
                    ReservedStock = 2,
                    AvailableStock = 8,
                    IsActive = true
                });
            }

            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateTenantClient(slug, tenantId, clientId);
        await client.PostAsJsonAsync("/api/v1/my-products", new AddMyProductRequest { ProductSku = baseSku });

        var response = await client.GetAsync("/api/v1/my-products?skip=0&limit=20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<PagedResult<MyProductDraftResult>>();
        Assert.NotNull(payload);
        var row = payload!.Items.Single(item => item.ProductSku == baseSku);
        Assert.Equal(variantB, row.ResolvedVariantSku);
        Assert.Equal(8, row.AvailableStock);
        Assert.Equal("AutoBestVariant", row.StockSource);
    }

    [Fact]
    public async Task List_WithVariantSkuFilter_ReturnsExactItem_WithPrefillFields()
    {
        const string tenantId = "tenant-a";
        const string slug = "sabr";
        var clientId = Guid.NewGuid();
        const string sku = "SKU-PREFILL-HTTP";

        await _factory.ResetDatabaseAsync();
        await SeedAsync(tenantId, slug, clientId, allowedSku: sku);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var product = await db.Products.SingleAsync(item => item.Sku == sku);
            product.Description = "Descricao prefill";
            product.Ean = "7891234567890";
            product.Ncm = "33049990";
            product.CostPriceCents = 2300;
            product.CatalogPriceCents = 4500;

            if (!await db.ProductVariants.AnyAsync(item => item.VariantSku == sku))
            {
                db.ProductVariants.Add(new ProductVariant
                {
                    VariantSku = sku,
                    BaseSku = sku,
                    Name = sku,
                    CostPriceCents = 2300,
                    CatalogPriceCents = 4500,
                    PhysicalStock = 0,
                    ReservedStock = 0,
                    AvailableStock = 0,
                    IsActive = true
                });
            }

            if (!await db.ProductImages.AnyAsync(item => item.ProductSku == sku))
            {
                db.ProductImages.Add(new ProductImage
                {
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

        using var client = _factory.CreateTenantClient(slug, tenantId, clientId);
        await client.PostAsJsonAsync("/api/v1/my-products", new AddMyProductRequest { ProductSku = sku });

        var response = await client.GetAsync($"/api/v1/my-products?variantSku={sku}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<PagedResult<MyProductDraftResult>>();
        Assert.NotNull(payload);
        Assert.Single(payload!.Items);
        var item = payload.Items[0];
        Assert.Equal(sku, item.ProductSku);
        Assert.Equal("Descricao prefill", item.Description);
        Assert.Equal("7891234567890", item.Gtin);
        Assert.Equal("33049990", item.Ncm);
        Assert.Equal("Nacional", item.Origin);
        Assert.Equal(23m, item.PurchaseCost);
        Assert.Equal(45m, item.CatalogPrice);
        Assert.Single(item.Images);
    }

    [Fact]
    public async Task List_WhenLimitIs200_ReturnsOk_AndLimit201ReturnsValidationError()
    {
        const string tenantId = "tenant-a";
        const string slug = "sabr";
        var clientId = Guid.NewGuid();

        await _factory.ResetDatabaseAsync();
        await SeedAsync(tenantId, slug, clientId, allowedSku: "SKU-550");
        using var client = _factory.CreateTenantClient(slug, tenantId, clientId);

        await client.PostAsJsonAsync("/api/v1/my-products", new AddMyProductRequest
        {
            ProductSku = "SKU-550"
        });

        var maxLimit = await client.GetAsync("/api/v1/my-products?skip=0&limit=200");
        Assert.Equal(HttpStatusCode.OK, maxLimit.StatusCode);
        var maxLimitResult = await maxLimit.Content.ReadFromJsonAsync<PagedResult<MyProductDraftResult>>();
        Assert.NotNull(maxLimitResult);
        Assert.True(maxLimitResult!.Items.Count >= 1);

        var aboveLimit = await client.GetAsync("/api/v1/my-products?skip=0&limit=201");
        Assert.Equal(HttpStatusCode.BadRequest, aboveLimit.StatusCode);
        var apiError = await aboveLimit.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(apiError);
        Assert.Equal("VALIDATION_ERROR", apiError!.Code);
        Assert.False(string.IsNullOrWhiteSpace(apiError.TraceId));
    }

    [Fact]
    public async Task IsolationAndAuthorization_AreEnforced()
    {
        const string tenantA = "tenant-a";
        const string tenantB = "tenant-b";
        const string slugA = "sabr";
        const string slugB = "orion";
        var clientA = Guid.NewGuid();
        var clientAOther = Guid.NewGuid();
        var clientB = Guid.NewGuid();

        await _factory.ResetDatabaseAsync();
        await SeedAsync(tenantA, slugA, clientA, allowedSku: "SKU-005", blockedSku: "SKU-900");
        await SeedAsync(tenantA, slugA, clientAOther, allowedSku: "SKU-005");
        await SeedAsync(tenantB, slugB, clientB, allowedSku: "SKU-005");
        using var clientTenantA = _factory.CreateTenantClient(slugA, tenantA, clientA);
        using var clientTenantAOther = _factory.CreateTenantClient(slugA, tenantA, clientAOther);
        using var clientTenantB = _factory.CreateTenantClient(slugB, tenantB, clientB);

        var create = await clientTenantA.PostAsJsonAsync("/api/v1/my-products", new AddMyProductRequest
        {
            ProductSku = "SKU-005"
        });
        var created = await create.Content.ReadFromJsonAsync<MyProductDraftResult>();
        Assert.NotNull(created);

        var crossTenant = await clientTenantB.GetAsync($"/api/v1/my-products/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);

        var crossClient = await clientTenantAOther.GetAsync($"/api/v1/my-products/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossClient.StatusCode);

        var unauthorizedSku = await clientTenantA.PostAsJsonAsync("/api/v1/my-products", new AddMyProductRequest
        {
            ProductSku = "SKU-900"
        });
        Assert.Equal(HttpStatusCode.Forbidden, unauthorizedSku.StatusCode);

        var error = await unauthorizedSku.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal("SKU_NOT_AUTHORIZED", error!.Code);
    }

    private async Task SeedAsync(string tenantId, string slug, Guid clientId, string allowedSku, string? blockedSku = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await TestDataSeeder.SeedTenantAsync(db, tenantId, slug);
        await TestDataSeeder.SeedClientCatalogGraphAsync(db, tenantId, clientId, allowedSku, blockedSku);
    }
}
