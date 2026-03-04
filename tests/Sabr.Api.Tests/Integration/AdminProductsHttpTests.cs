using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sabr.Api.Tests.TestHost;
using Sabr.Application.Models;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;
using Sabr.Infrastructure.Persistence;

namespace Sabr.Api.Tests.Integration;

public sealed class AdminProductsHttpTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AdminProductsHttpTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AdminProducts_CreateGetListAndDeactivate_AreConsistent()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateAdminClient();

        var createResponse = await client.PostAsJsonAsync("/api/v1/admin/products", new AdminProductUpsertRequest
        {
            Sku = "abc-01",
            Name = "Produto ABC",
            Brand = "Marca ABC",
            ThumbnailUrl = null,
            CostPriceCents = 1050,
            CatalogPriceCents = 1590,
            IsActive = false
        });

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        var getResponse = await client.GetAsync("/api/v1/admin/products/abc-01");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var product = await getResponse.Content.ReadFromJsonAsync<AdminProductResult>();
        Assert.NotNull(product);
        Assert.Equal("ABC-01", product!.Sku);

        var listResponse = await client.GetAsync("/api/v1/admin/products?skip=0&limit=20&search=produto%20abc");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<PagedResult<AdminProductResult>>();
        Assert.NotNull(list);
        Assert.Contains(list!.Items, item => item.Sku == "ABC-01");

        var deleteFirst = await client.DeleteAsync("/api/v1/admin/products/ABC-01");
        Assert.Equal(HttpStatusCode.NoContent, deleteFirst.StatusCode);

        var deleteSecond = await client.DeleteAsync("/api/v1/admin/products/abc-01");
        Assert.Equal(HttpStatusCode.NoContent, deleteSecond.StatusCode);

        var activeOnly = await client.GetAsync("/api/v1/admin/products?skip=0&limit=20&isActive=true&search=abc-01");
        Assert.Equal(HttpStatusCode.OK, activeOnly.StatusCode);
        var activeList = await activeOnly.Content.ReadFromJsonAsync<PagedResult<AdminProductResult>>();
        Assert.NotNull(activeList);
        Assert.DoesNotContain(activeList!.Items, item => item.Sku == "ABC-01");
    }

    [Fact]
    public async Task AdminProducts_GetByLowercaseSku_ResolvesUppercasePersistedValue()
    {
        await _factory.ResetDatabaseAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Products.Add(new Product
            {
                Sku = "sku-xpto",
                Name = "Produto XPTO",
                Brand = "Marca XPTO",
                CostPriceCents = 500,
                CatalogPriceCents = 900,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAdminClient();
        var response = await client.GetAsync("/api/v1/admin/products/sku-xpto");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var product = await response.Content.ReadFromJsonAsync<AdminProductResult>();
        Assert.NotNull(product);
        Assert.Equal("SKU-XPTO", product!.Sku);
    }

    [Fact]
    public async Task AdminProducts_GetMissingSku_ReturnsProductNotFoundWithTraceId()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateAdminClient();

        var response = await client.GetAsync("/api/v1/admin/products/SKU-NOT-FOUND");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var apiError = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(apiError);
        Assert.Equal("PRODUCT_NOT_FOUND", apiError!.Code);
        Assert.False(string.IsNullOrWhiteSpace(apiError.TraceId));
    }

    [Fact]
    public async Task AdminProducts_UpdateToActiveWithoutCatalogs_Returns422ProductMissingCatalogLinks()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-a";
        const string tenantSlug = "sabr";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await TestDataSeeder.SeedTenantAsync(db, tenantId, tenantSlug);
            db.Products.Add(new Product
            {
                Sku = "SKU-NO-CAT",
                Name = "Sem Catalogo",
                Brand = "Marca",
                CostPriceCents = 100,
                CatalogPriceCents = 200,
                IsActive = false
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAdminClient();
        var response = await client.PutAsJsonAsync("/api/v1/admin/products/SKU-NO-CAT", new AdminProductUpdateRequest
        {
            IsActive = true,
            TenantSlug = tenantSlug
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var apiError = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(apiError);
        Assert.Equal("PRODUCT_MISSING_CATALOG_LINKS", apiError!.Code);
        Assert.False(string.IsNullOrWhiteSpace(apiError.TraceId));
    }

    [Fact]
    public async Task AdminProducts_RequiresAnatelWithoutNumber_Returns422AnatelRequired()
    {
        await _factory.ResetDatabaseAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Products.Add(new Product
            {
                Sku = "SKU-ANATEL",
                Name = "Produto Anatel",
                Brand = "Marca Anatel",
                CostPriceCents = 500,
                CatalogPriceCents = 900,
                IsActive = false
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAdminClient();
        var response = await client.PutAsJsonAsync("/api/v1/admin/products/SKU-ANATEL", new AdminProductUpdateRequest
        {
            RequiresAnatel = true,
            AnatelHomologationNumber = null
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var apiError = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(apiError);
        Assert.Equal("ANATEL_REQUIRED", apiError!.Code);
        Assert.False(string.IsNullOrWhiteSpace(apiError.TraceId));
    }

    [Fact]
    public async Task AdminProducts_CreateWithoutCategory_UsesUncategorizedFallback()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateAdminClient();

        var createResponse = await client.PostAsJsonAsync("/api/v1/admin/products", new AdminProductUpsertRequest
        {
            Sku = "sku-cat-fallback",
            Name = "Produto sem categoria",
            Brand = "Marca Fallback",
            Ncm = null,
            Ean = null,
            Description = null,
            CategoryId = null,
            CostPriceCents = 1000,
            CatalogPriceCents = 1500,
            IsActive = false
        });

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        var getResponse = await client.GetAsync("/api/v1/admin/products/SKU-CAT-FALLBACK");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var product = await getResponse.Content.ReadFromJsonAsync<AdminProductResult>();
        Assert.NotNull(product);
        Assert.Equal("uncategorized", product!.CategoryId);
    }

    [Fact]
    public async Task AdminProducts_UpdateWithoutCategoryIdField_KeepsCurrentCategory()
    {
        await _factory.ResetDatabaseAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Categories.Add(new Category
            {
                Name = "Hardware",
                Slug = "hardware",
                IsActive = true
            });
            db.Products.Add(new Product
            {
                Sku = "SKU-CAT-KEEP",
                Name = "Produto Categoria",
                Brand = "Marca Categoria",
                CategoryId = "hardware",
                CostPriceCents = 1000,
                CatalogPriceCents = 1500,
                IsActive = false
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAdminClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/admin/products/SKU-CAT-KEEP")
        {
            Content = new StringContent("{\"name\":\"Produto Atualizado\"}", Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<AdminProductResult>();
        Assert.NotNull(updated);
        Assert.Equal("hardware", updated!.CategoryId);
    }

    [Fact]
    public async Task AdminProducts_UpdateWithCategoryIdNullOrEmpty_FallsBackToUncategorized()
    {
        await _factory.ResetDatabaseAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Categories.Add(new Category
            {
                Name = "Audio",
                Slug = "audio",
                IsActive = true
            });
            db.Products.Add(new Product
            {
                Sku = "SKU-CAT-NULL",
                Name = "Produto Null",
                Brand = "Marca Null",
                CategoryId = "audio",
                CostPriceCents = 1000,
                CatalogPriceCents = 1500,
                IsActive = false
            });
            db.Products.Add(new Product
            {
                Sku = "SKU-CAT-EMPTY",
                Name = "Produto Empty",
                Brand = "Marca Empty",
                CategoryId = "audio",
                CostPriceCents = 1000,
                CatalogPriceCents = 1500,
                IsActive = false
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAdminClient();

        using (var nullRequest = new HttpRequestMessage(HttpMethod.Put, "/api/v1/admin/products/SKU-CAT-NULL")
        {
            Content = new StringContent("{\"categoryId\":null}", Encoding.UTF8, "application/json")
        })
        {
            var nullResponse = await client.SendAsync(nullRequest);
            Assert.Equal(HttpStatusCode.OK, nullResponse.StatusCode);
            var nullUpdated = await nullResponse.Content.ReadFromJsonAsync<AdminProductResult>();
            Assert.NotNull(nullUpdated);
            Assert.Equal("uncategorized", nullUpdated!.CategoryId);
        }

        using (var emptyRequest = new HttpRequestMessage(HttpMethod.Put, "/api/v1/admin/products/SKU-CAT-EMPTY")
        {
            Content = new StringContent("{\"categoryId\":\"\"}", Encoding.UTF8, "application/json")
        })
        {
            var emptyResponse = await client.SendAsync(emptyRequest);
            Assert.Equal(HttpStatusCode.OK, emptyResponse.StatusCode);
            var emptyUpdated = await emptyResponse.Content.ReadFromJsonAsync<AdminProductResult>();
            Assert.NotNull(emptyUpdated);
            Assert.Equal("uncategorized", emptyUpdated!.CategoryId);
        }
    }

    [Fact]
    public async Task AdminProducts_UpdateWithUnknownCategory_Returns422CategoryNotFound()
    {
        await _factory.ResetDatabaseAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Products.Add(new Product
            {
                Sku = "SKU-CAT-NOTFOUND",
                Name = "Produto NotFound",
                Brand = "Marca NotFound",
                CategoryId = "uncategorized",
                CostPriceCents = 1000,
                CatalogPriceCents = 1500,
                IsActive = false
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAdminClient();
        var response = await client.PutAsJsonAsync("/api/v1/admin/products/SKU-CAT-NOTFOUND", new AdminProductUpdateRequest
        {
            CategoryId = "does-not-exist"
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var apiError = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(apiError);
        Assert.Equal("CATEGORY_NOT_FOUND", apiError!.Code);
        Assert.False(string.IsNullOrWhiteSpace(apiError.TraceId));
    }

    [Fact]
    public async Task AdminProducts_UpdateWithInactiveCategory_Returns422CategoryInactive()
    {
        await _factory.ResetDatabaseAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Categories.Add(new Category
            {
                Name = "Inativa",
                Slug = "cat-inativa",
                IsActive = false
            });
            db.Products.Add(new Product
            {
                Sku = "SKU-CAT-INACTIVE",
                Name = "Produto Inactive",
                Brand = "Marca Inactive",
                CategoryId = "uncategorized",
                CostPriceCents = 1000,
                CatalogPriceCents = 1500,
                IsActive = false
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAdminClient();
        var response = await client.PutAsJsonAsync("/api/v1/admin/products/SKU-CAT-INACTIVE", new AdminProductUpdateRequest
        {
            CategoryId = "cat-inativa"
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var apiError = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(apiError);
        Assert.Equal("CATEGORY_INACTIVE", apiError!.Code);
        Assert.False(string.IsNullOrWhiteSpace(apiError.TraceId));
    }

    [Fact]
    public async Task AdminProducts_List_WithPaginationLimit200And201_UsesCentralPolicy()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateAdminClient();

        var limit200 = await client.GetAsync("/api/v1/admin/products?skip=0&limit=200");
        Assert.Equal(HttpStatusCode.OK, limit200.StatusCode);

        var limit201 = await client.GetAsync("/api/v1/admin/products?skip=0&limit=201");
        Assert.Equal(HttpStatusCode.BadRequest, limit201.StatusCode);
        var apiError = await limit201.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(apiError);
        Assert.Equal("VALIDATION_ERROR", apiError!.Code);
        Assert.False(string.IsNullOrWhiteSpace(apiError.TraceId));
    }

    [Fact]
    public async Task AdminProducts_Update_OverwritesEligibleListingDrafts_FromAdminData()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-sync-01";
        var clientId = Guid.NewGuid();
        var integrationId = Guid.NewGuid();
        const long sellerId = 1979655640;
        const string baseSku = "SKU-SYNC-01";
        const string variantSku = "SKU-SYNC-01-V1";

        var oldProviderDraftJson = JsonSerializer.Serialize(new
        {
            title = "Titulo antigo",
            description = "Descricao antiga",
            gtin = "0000000000000",
            emptyGtinReason = "EMPTY_GTIN_REASON",
            ncm = "00000000",
            origin = "Importado",
            productCostCents = 500L,
            images = new[]
            {
                new { url = "https://old.example/img-old.jpg", position = 1 }
            }
        });

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Products.Add(new Product
            {
                Sku = baseSku,
                Name = "Nome antigo",
                Brand = "Marca",
                Ean = "1111111111111",
                Ncm = "11111111",
                Description = "Descricao antiga",
                CostPriceCents = 900,
                CatalogPriceCents = 1500,
                IsActive = false
            });
            db.ProductVariants.Add(new ProductVariant
            {
                VariantSku = variantSku,
                BaseSku = baseSku,
                Name = "Variante Azul",
                CostPriceCents = 1234,
                CatalogPriceCents = 1999,
                PhysicalStock = 10,
                ReservedStock = 0,
                AvailableStock = 10,
                IsActive = true
            });
            db.ProductImages.AddRange(
                new ProductImage
                {
                    ProductSku = baseSku,
                    Url = "https://new.example/img-primary.jpg",
                    MimeType = "image/jpeg",
                    SizeBytes = 100,
                    SortOrder = 1,
                    IsPrimary = true
                },
                new ProductImage
                {
                    ProductSku = baseSku,
                    Url = "https://new.example/img-second.jpg",
                    MimeType = "image/jpeg",
                    SizeBytes = 120,
                    SortOrder = 2,
                    IsPrimary = false
                });
            db.ListingDrafts.AddRange(
                new ListingDraft
                {
                    DraftId = Guid.NewGuid(),
                    TenantId = tenantId,
                    ClientId = clientId,
                    Provider = MarketplaceProvider.MercadoLivre,
                    IntegrationId = integrationId,
                    SellerId = sellerId,
                    BaseProductSku = baseSku,
                    SabrVariantSku = variantSku,
                    CurrencyId = "BRL",
                    Status = ListingDraftStatus.Draft,
                    ProviderDraftJson = oldProviderDraftJson
                },
                new ListingDraft
                {
                    DraftId = Guid.NewGuid(),
                    TenantId = tenantId,
                    ClientId = clientId,
                    Provider = MarketplaceProvider.MercadoLivre,
                    IntegrationId = integrationId,
                    SellerId = sellerId,
                    BaseProductSku = baseSku,
                    SabrVariantSku = variantSku,
                    CurrencyId = "BRL",
                    Status = ListingDraftStatus.Valid,
                    ProviderDraftJson = oldProviderDraftJson
                },
                new ListingDraft
                {
                    DraftId = Guid.NewGuid(),
                    TenantId = tenantId,
                    ClientId = clientId,
                    Provider = MarketplaceProvider.MercadoLivre,
                    IntegrationId = integrationId,
                    SellerId = sellerId,
                    BaseProductSku = baseSku,
                    SabrVariantSku = variantSku,
                    CurrencyId = "BRL",
                    Status = ListingDraftStatus.Error,
                    ProviderDraftJson = oldProviderDraftJson
                },
                new ListingDraft
                {
                    DraftId = Guid.NewGuid(),
                    TenantId = tenantId,
                    ClientId = clientId,
                    Provider = MarketplaceProvider.MercadoLivre,
                    IntegrationId = integrationId,
                    SellerId = sellerId,
                    BaseProductSku = baseSku,
                    SabrVariantSku = variantSku,
                    CurrencyId = "BRL",
                    Status = ListingDraftStatus.Published,
                    ProviderDraftJson = oldProviderDraftJson,
                    PublishedItemId = "MLB123"
                });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAdminClient();
        var response = await client.PutAsJsonAsync($"/api/v1/admin/products/{baseSku}", new AdminProductUpdateRequest
        {
            Name = "Nome novo",
            Ean = "7892864163877",
            Ncm = "40149010",
            Description = "Descricao nova",
            CostPriceCents = 3000,
            CatalogPriceCents = 4500
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var drafts = await verifyDb.ListingDrafts
            .Where(item => item.BaseProductSku == baseSku)
            .ToListAsync();

        Assert.Equal(4, drafts.Count);

        var syncedDrafts = drafts
            .Where(item => item.Status == ListingDraftStatus.Draft && item.PublishedItemId == null)
            .ToList();
        var error = drafts.Single(item => item.Status == ListingDraftStatus.Error);
        var published = drafts.Single(item => item.Status == ListingDraftStatus.Published);

        // One draft started as Valid and must be downgraded to Draft after forced sync.
        Assert.Equal(2, syncedDrafts.Count);

        static void AssertSyncedDraftPayload(string providerDraftJson)
        {
            using var doc = JsonDocument.Parse(providerDraftJson);
            var root = doc.RootElement;

            Assert.Equal("Nome novo - Variante Azul", root.GetProperty("title").GetString());
            Assert.Equal("Descricao nova", root.GetProperty("description").GetString());
            Assert.Equal("7892864163877", root.GetProperty("gtin").GetString());
            Assert.Equal("40149010", root.GetProperty("ncm").GetString());
            Assert.Equal("Nacional", root.GetProperty("origin").GetString());
            Assert.Equal(1234, root.GetProperty("productCostCents").GetInt64());
            Assert.True(root.TryGetProperty("emptyGtinReason", out var emptyReasonProperty));
            Assert.Equal(JsonValueKind.Null, emptyReasonProperty.ValueKind);

            var images = root.GetProperty("images");
            Assert.Equal(2, images.GetArrayLength());
            Assert.Equal("https://new.example/img-primary.jpg", images[0].GetProperty("url").GetString());
            Assert.Equal(1, images[0].GetProperty("position").GetInt32());
            Assert.Equal("https://new.example/img-second.jpg", images[1].GetProperty("url").GetString());
            Assert.Equal(2, images[1].GetProperty("position").GetInt32());
        }

        foreach (var item in syncedDrafts)
        {
            AssertSyncedDraftPayload(item.ProviderDraftJson);
        }
        AssertSyncedDraftPayload(error.ProviderDraftJson);

        using (var publishedDoc = JsonDocument.Parse(published.ProviderDraftJson))
        {
            Assert.Equal("Titulo antigo", publishedDoc.RootElement.GetProperty("title").GetString());
            Assert.Equal("0000000000000", publishedDoc.RootElement.GetProperty("gtin").GetString());
            Assert.Equal("00000000", publishedDoc.RootElement.GetProperty("ncm").GetString());
        }
    }

    [Fact]
    public async Task AdminProducts_ImagesRejectWebpAndGif_AcceptSvgPngJpeg_AndEnforceLimit()
    {
        await _factory.ResetDatabaseAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Products.Add(new Product
            {
                Sku = "SKU-IMG-01",
                Name = "Produto Imagem",
                Brand = "Marca Img",
                CostPriceCents = 1000,
                CatalogPriceCents = 1500,
                IsActive = false
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAdminClient();

        static MultipartFormDataContent BuildUpload(string fileName, string contentType, string payload = "file-data")
        {
            var content = new MultipartFormDataContent();
            var bytes = Encoding.UTF8.GetBytes(payload);
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            content.Add(file, "file", fileName);
            return content;
        }

        using (var webp = BuildUpload("photo.webp", "image/webp"))
        {
            var response = await client.PostAsync("/api/v1/admin/products/SKU-IMG-01/images", webp);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var apiError = await response.Content.ReadFromJsonAsync<ApiError>();
            Assert.NotNull(apiError);
            Assert.Equal("INVALID_IMAGE_TYPE", apiError!.Code);
        }

        using (var gif = BuildUpload("photo.gif", "image/gif"))
        {
            var response = await client.PostAsync("/api/v1/admin/products/SKU-IMG-01/images", gif);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var apiError = await response.Content.ReadFromJsonAsync<ApiError>();
            Assert.NotNull(apiError);
            Assert.Equal("INVALID_IMAGE_TYPE", apiError!.Code);
        }

        using (var pngUpload = BuildUpload("image-1.png", "image/png"))
        {
            var response = await client.PostAsync("/api/v1/admin/products/SKU-IMG-01/images", pngUpload);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<ProductImageResult>();
            Assert.NotNull(result);
            Assert.Equal("SKU-IMG-01", result!.ProductSku);
            Assert.False(string.IsNullOrWhiteSpace(result.Url));

            var imageResponse = await client.GetAsync(result.Url);
            Assert.Equal(HttpStatusCode.OK, imageResponse.StatusCode);
            Assert.Equal("image/png", imageResponse.Content.Headers.ContentType?.MediaType);
        }

        var accepted = new[]
        {
            BuildUpload("image-2.jpg", "image/jpeg"),
            BuildUpload("image-3.svg", "image/svg+xml", "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")
        };

        foreach (var upload in accepted)
        {
            using (upload)
            {
                var response = await client.PostAsync("/api/v1/admin/products/SKU-IMG-01/images", upload);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                var result = await response.Content.ReadFromJsonAsync<ProductImageResult>();
                Assert.NotNull(result);
                Assert.Equal("SKU-IMG-01", result!.ProductSku);
            }
        }

        for (var i = 0; i < 7; i++)
        {
            using var upload = BuildUpload($"img-{i + 4}.png", "image/png");
            var response = await client.PostAsync("/api/v1/admin/products/SKU-IMG-01/images", upload);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using (var overLimit = BuildUpload("img-over.png", "image/png"))
        {
            var response = await client.PostAsync("/api/v1/admin/products/SKU-IMG-01/images", overLimit);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var apiError = await response.Content.ReadFromJsonAsync<ApiError>();
            Assert.NotNull(apiError);
            Assert.Equal("IMAGE_LIMIT_EXCEEDED", apiError!.Code);
        }
    }
}
