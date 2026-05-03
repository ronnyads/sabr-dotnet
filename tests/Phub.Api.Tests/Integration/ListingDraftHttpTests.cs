using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Phub.Api.Tests.TestHost;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Domain.Protheus;
using Phub.Infrastructure.Persistence;

namespace Phub.Api.Tests.Integration;

public sealed class ListingDraftHttpTests : IClassFixture<MercadoLivreTestWebApplicationFactory>
{
    private readonly MercadoLivreTestWebApplicationFactory _factory;

    public ListingDraftHttpTests(MercadoLivreTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Upsert_UsesNaturalKey_NoDuplicate_AndSupportsPatchWithClearFields()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-01";
        const string tenantSlug = "tenantdraft01";
        var clientId = Guid.NewGuid();
        const long sellerId = 1010001;
        const string baseSku = "SKU-BASE-DR-01";
        const string variantSku = "SKU-VAR-DR-01";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductVariantAsync(baseSku, variantSku, includeImage: true);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var first = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/upsert",
            new ListingDraftUpsertRequest
            {
                IntegrationId = integrationId,
                SabrVariantSku = variantSku,
                CategoryId = "MLB1055",
                ListingTypeId = "gold_special",
                Price = 129.99m,
                CurrencyId = "BRL"
            });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstPayload = await first.Content.ReadFromJsonAsync<ListingDraftResult>();
        Assert.NotNull(firstPayload);
        Assert.False(string.IsNullOrWhiteSpace(firstPayload!.RowVersion));
        _ = Convert.FromBase64String(firstPayload.RowVersion);

        var second = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/upsert",
            new ListingDraftUpsertRequest
            {
                IntegrationId = integrationId,
                SabrVariantSku = variantSku,
                Price = 139.99m
            });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var count = await db.ListingDrafts.CountAsync(item =>
                item.TenantId == tenantId &&
                item.ClientId == clientId &&
                item.IntegrationId == integrationId &&
                item.SabrVariantSku == variantSku);
            Assert.Equal(1, count);
        }

        var secondPayload = await second.Content.ReadFromJsonAsync<ListingDraftResult>();
        Assert.NotNull(secondPayload);
        Assert.Equal("MLB1055", secondPayload!.CategoryId);

        var keepCategory = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/upsert",
            new ListingDraftUpsertRequest
            {
                DraftId = secondPayload.DraftId,
                CategoryId = null
            });
        var keepPayload = await keepCategory.Content.ReadFromJsonAsync<ListingDraftResult>();
        Assert.NotNull(keepPayload);
        Assert.Equal("MLB1055", keepPayload!.CategoryId);

        var clearCategory = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/upsert",
            new ListingDraftUpsertRequest
            {
                DraftId = secondPayload.DraftId,
                ClearFields = new List<string> { "categoryId" }
            });
        var clearPayload = await clearCategory.Content.ReadFromJsonAsync<ListingDraftResult>();
        Assert.NotNull(clearPayload);
        Assert.Null(clearPayload!.CategoryId);
    }

    [Fact]
    public async Task Upsert_WhenManualCategoryIsProvided_PersistsCategoryLock()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-lock-01";
        const string tenantSlug = "tenantdraftlock01";
        var clientId = Guid.NewGuid();
        const long sellerId = 1010111;
        const string baseSku = "SKU-BASE-LOCK-01";
        const string variantSku = "SKU-VAR-LOCK-01";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductVariantAsync(baseSku, variantSku, includeImage: true, categorySlug: "geladeiras-termicas");
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/upsert",
            new ListingDraftUpsertRequest
            {
                IntegrationId = integrationId,
                SabrVariantSku = variantSku,
                CategoryId = "MLB18272",
                ListingTypeId = "gold_special",
                Price = 129.99m,
                CurrencyId = "BRL"
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var categoryLock = await db.ProductMarketplaceCategoryLocks.FirstOrDefaultAsync(item =>
            item.TenantId == tenantId &&
            item.ClientId == clientId &&
            item.BaseProductSku == baseSku &&
            item.SiteId == "MLB");
        Assert.NotNull(categoryLock);
        Assert.Equal("MLB18272", categoryLock!.ApprovedCategoryId);
        Assert.Equal(MarketplaceCategoryLockStatus.ApprovedManual, categoryLock.Status);
        Assert.Equal(MarketplaceCategoryLockSource.Manual, categoryLock.Source);
    }

    [Fact]
    public async Task DraftsGet_WhenVariantMissingButProductExists_BackfillsVariantAndReturns200()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-miss-01";
        const string tenantSlug = "tenantdraftmiss01";
        var clientId = Guid.NewGuid();
        const string missingSku = "SKU-MISS-DR-01";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await db.Products.AnyAsync(item => item.Sku == missingSku))
            {
                db.Products.Add(new Product
                {
                    Sku = missingSku,
                    Name = "Produto sem variante",
                    Brand = "Marca Teste",
                    CostPriceCents = 1000,
                    CatalogPriceCents = 1500,
                    IsActive = true
                });
            }

            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/get",
            new ListingDraftGetRequest
            {
                Channel = "mercadolivre",
                VariantSku = missingSku
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ListingDraftGetResult>();
        Assert.NotNull(payload);
        Assert.NotNull(payload!.Candidates);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var variant = await verifyDb.ProductVariants.AsNoTracking().SingleOrDefaultAsync(item => item.VariantSku == missingSku);
        Assert.NotNull(variant);
        Assert.Equal(0, variant!.PhysicalStock);
        Assert.Equal(0, variant.ReservedStock);
        Assert.Equal(0, variant.AvailableStock);
    }

    [Fact]
    public async Task DraftsGet_WhenRequestIsBaseSkuWithActiveVariants_UsesAutoBestVariant()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-base-01";
        const string tenantSlug = "tenantdraftbase01";
        var clientId = Guid.NewGuid();
        const string baseSku = "SKU-BASE-DR-BEST-01";
        const string variantA = "SKU-VAR-DR-BEST-01-A";
        const string variantB = "SKU-VAR-DR-BEST-01-B";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductVariantAsync(baseSku, variantA, includeImage: true, physicalStock: 5, reservedStock: 2);
        await SeedProductVariantAsync(baseSku, variantB, includeImage: false, physicalStock: 12, reservedStock: 2);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/get",
            new ListingDraftGetRequest
            {
                Channel = "mercadolivre",
                VariantSku = baseSku
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ListingDraftGetResult>();
        Assert.NotNull(payload);
        Assert.Equal(variantB, payload!.ResolvedVariantSku);
        Assert.Equal(10, payload.AvailableStock);
        Assert.Equal("AutoBestVariant", payload.StockSource);
    }

    [Fact]
    public async Task DraftsGet_WithoutMlAccessToken_ReturnsSelectionRequiredMlUnavailable()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-suggest-01";
        const string tenantSlug = "tenantdraftsuggest01";
        var clientId = Guid.NewGuid();
        const string baseSku = "SKU-BASE-DR-SUG-01";
        const string variantSku = "SKU-VAR-DR-SUG-01";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductVariantAsync(baseSku, variantSku, includeImage: true, categorySlug: "ml-test-slug");

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/get",
            new ListingDraftGetRequest
            {
                Channel = "mercadolivre",
                VariantSku = variantSku
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ListingDraftGetResult>();
        Assert.NotNull(payload);
        Assert.Null(payload!.Draft);
        Assert.Null(payload.SuggestedCategoryId);
        Assert.True(payload.CategorySelectionRequired);
        Assert.Equal("ML_UNAVAILABLE", payload.CategoryResolutionReason);
    }

    [Fact]
    public async Task DraftsGet_ForTratamentosDeBelezaWithoutMlAccessToken_ReturnsSelectionRequiredMlUnavailable()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-suggest-03";
        const string tenantSlug = "tenantdraftsuggest03";
        var clientId = Guid.NewGuid();
        const string baseSku = "SKU-BASE-DR-SUG-03";
        const string variantSku = "SKU-VAR-DR-SUG-03";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductVariantAsync(baseSku, variantSku, includeImage: true, categorySlug: "tratamentos-de-beleza");

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/get",
            new ListingDraftGetRequest
            {
                Channel = "mercadolivre",
                VariantSku = variantSku
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ListingDraftGetResult>();
        Assert.NotNull(payload);
        Assert.Null(payload!.Draft);
        Assert.Null(payload.SuggestedCategoryId);
        Assert.True(payload.CategorySelectionRequired);
        Assert.Equal("ML_UNAVAILABLE", payload.CategoryResolutionReason);
    }

    [Fact]
    public async Task DraftsGet_ForGeladeirasTermicasWithoutMlAccessToken_ReturnsSelectionRequiredMlUnavailable()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-suggest-04";
        const string tenantSlug = "tenantdraftsuggest04";
        var clientId = Guid.NewGuid();
        const string baseSku = "SKU-BASE-DR-SUG-04";
        const string variantSku = "SKU-VAR-DR-SUG-04";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductVariantAsync(baseSku, variantSku, includeImage: true, categorySlug: "geladeiras-termicas");

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/get",
            new ListingDraftGetRequest
            {
                Channel = "mercadolivre",
                VariantSku = variantSku
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ListingDraftGetResult>();
        Assert.NotNull(payload);
        Assert.Null(payload!.Draft);
        Assert.Null(payload.SuggestedCategoryId);
        Assert.True(payload.CategorySelectionRequired);
        Assert.Equal("ML_UNAVAILABLE", payload.CategoryResolutionReason);
    }

    [Fact]
    public async Task DraftsGet_ReturnsSuggestedCategoryId_WhenDraftCategoryIsLegacyInvalidForSite()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-suggest-legacy-01";
        const string tenantSlug = "tenantdraftsuggestlegacy01";
        var clientId = Guid.NewGuid();
        const long sellerId = 1010202;
        const string baseSku = "SKU-BASE-DR-SUG-LEG-01";
        const string variantSku = "SKU-VAR-DR-SUG-LEG-01";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductVariantAsync(baseSku, variantSku, includeImage: true, categorySlug: "ml-test-slug");
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        _factory.FakeMercadoLivreApiClient.DomainDiscoverySuggestions = new List<MercadoLivreDomainDiscoverySuggestion>
        {
            new()
            {
                CategoryId = "MLB1055",
                CategoryName = "Massagem e Relaxamento",
                PathFromRoot = "Beleza > Tratamentos de Beleza > Massagem e Relaxamento"
            }
        };

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var upsertResponse = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/upsert",
            new ListingDraftUpsertRequest
            {
                IntegrationId = integrationId,
                SellerId = sellerId.ToString(),
                SabrVariantSku = variantSku,
                SiteId = "MLB",
                CategoryId = "MASSAG",
                ListingTypeId = "gold_special",
                Price = 129.99m,
                CurrencyId = "BRL"
            });

        Assert.Equal(HttpStatusCode.OK, upsertResponse.StatusCode);
        var upsertPayload = await upsertResponse.Content.ReadFromJsonAsync<ListingDraftResult>();
        Assert.NotNull(upsertPayload);
        Assert.Equal("MASSAG", upsertPayload!.CategoryId);

        var getResponse = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/get",
            new ListingDraftGetRequest
            {
                Channel = "mercadolivre",
                VariantSku = variantSku,
                SellerId = sellerId.ToString(),
                IntegrationId = integrationId
            });

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getPayload = await getResponse.Content.ReadFromJsonAsync<ListingDraftGetResult>();
        Assert.NotNull(getPayload);
        Assert.NotNull(getPayload!.Draft);
        Assert.Equal("MASSAG", getPayload.Draft!.CategoryId);
        Assert.Equal("MLB1055", getPayload.SuggestedCategoryId);
        Assert.Equal("REVIEW_REQUIRED_STALE_DRAFT", getPayload.CategoryResolutionReason);
    }

    [Fact]
    public async Task DraftsGet_ReturnsSuggestedCategoryId_WhenDraftCategoryIsValidButAdminMappingChanged_ToGeladeirasTermicas()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-suggest-admin-01";
        const string tenantSlug = "tenantdraftsuggestadmin01";
        var clientId = Guid.NewGuid();
        const long sellerId = 1010205;
        const string baseSku = "SKU-BASE-DR-SUG-ADM-01";
        const string variantSku = "SKU-VAR-DR-SUG-ADM-01";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductVariantAsync(baseSku, variantSku, includeImage: true, categorySlug: "tratamentos-de-beleza");
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        _factory.FakeMercadoLivreApiClient.DomainDiscoverySuggestions = new List<MercadoLivreDomainDiscoverySuggestion>
        {
            new()
            {
                CategoryId = "MLB18272",
                CategoryName = "Geladeiras Térmicas",
                PathFromRoot = "Esportes e Fitness > Camping, Caça e Pesca > Acessórios de Camping > Recipientes Térmicos > Geladeiras Térmicas"
            }
        };

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var upsertResponse = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/upsert",
            new ListingDraftUpsertRequest
            {
                IntegrationId = integrationId,
                SellerId = sellerId.ToString(),
                SabrVariantSku = variantSku,
                SiteId = "MLB",
                CategoryId = "MLB277912",
                ListingTypeId = "gold_special",
                Price = 129.99m,
                CurrencyId = "BRL"
            });

        Assert.Equal(HttpStatusCode.OK, upsertResponse.StatusCode);

        await SeedProductVariantAsync(baseSku, variantSku, includeImage: true, categorySlug: "geladeiras-termicas");

        var getResponse = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/get",
            new ListingDraftGetRequest
            {
                Channel = "mercadolivre",
                VariantSku = variantSku,
                SellerId = sellerId.ToString(),
                IntegrationId = integrationId
            });

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getPayload = await getResponse.Content.ReadFromJsonAsync<ListingDraftGetResult>();
        Assert.NotNull(getPayload);
        Assert.NotNull(getPayload!.Draft);
        Assert.Equal("MLB277912", getPayload.Draft!.CategoryId);
        Assert.Equal("MLB18272", getPayload.SuggestedCategoryId);
        Assert.NotNull(getPayload.SuggestedCategorySource);
        Assert.Equal("domain_discovery", getPayload.SuggestedCategorySource);
        Assert.Equal("ReviewRequired", getPayload.CategoryResolutionStatus);
        Assert.Equal("REVIEW_REQUIRED_STALE_DRAFT", getPayload.CategoryResolutionReason);
        Assert.True(getPayload.CategorySelectionRequired);
        Assert.Contains(getPayload.CategorySuggestions, item => item.CategoryId == "MLB18272");
    }

    [Fact]
    public async Task Upsert_AppliesAutofillCategory_WhenDraftCreatedWithoutCategory()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-suggest-02";
        const string tenantSlug = "tenantdraftsuggest02";
        var clientId = Guid.NewGuid();
        const long sellerId = 1010201;
        const string baseSku = "SKU-BASE-DR-SUG-02";
        const string variantSku = "SKU-VAR-DR-SUG-02";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductVariantAsync(baseSku, variantSku, includeImage: true, categorySlug: "ml-test-slug");
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        _factory.FakeMercadoLivreApiClient.DomainDiscoverySuggestions = new List<MercadoLivreDomainDiscoverySuggestion>
        {
            new()
            {
                CategoryId = "MLB1055",
                CategoryName = "Massagem e Relaxamento",
                PathFromRoot = "Beleza > Tratamentos de Beleza > Massagem e Relaxamento"
            }
        };

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/upsert",
            new ListingDraftUpsertRequest
            {
                IntegrationId = integrationId,
                SabrVariantSku = variantSku,
                ListingTypeId = "gold_special",
                Price = 129.99m,
                CurrencyId = "BRL"
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ListingDraftResult>();
        Assert.NotNull(payload);
        Assert.Equal("MLB1055", payload!.CategoryId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lockEntry = await db.ProductMarketplaceCategoryLocks.FirstOrDefaultAsync(item =>
            item.TenantId == tenantId &&
            item.ClientId == clientId &&
            item.BaseProductSku == baseSku &&
            item.SiteId == "MLB");
        Assert.NotNull(lockEntry);
        Assert.Equal("MLB1055", lockEntry!.ApprovedCategoryId);
        Assert.Equal(MarketplaceCategoryLockStatus.ApprovedAuto, lockEntry.Status);
    }

    [Fact]
    public async Task Upsert_PrioritizesSiteSpecificAutofill_WhenSiteSpecificMappingExists()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-suggest-02b";
        const string tenantSlug = "tenantdraftsuggest02b";
        var clientId = Guid.NewGuid();
        const long sellerId = 1010204;
        const string baseSku = "SKU-BASE-DR-SUG-02B";
        const string variantSku = "SKU-VAR-DR-SUG-02B";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductVariantAsync(baseSku, variantSku, includeImage: true, categorySlug: "ml-test-slug");
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        _factory.FakeMercadoLivreApiClient.DomainDiscoverySuggestions = new List<MercadoLivreDomainDiscoverySuggestion>
        {
            new()
            {
                CategoryId = "MLA1055",
                CategoryName = "Massagem e Relaxamento",
                PathFromRoot = "Beleza > Tratamientos de Belleza > Masajes y Relajación"
            }
        };

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/upsert",
            new ListingDraftUpsertRequest
            {
                IntegrationId = integrationId,
                SabrVariantSku = variantSku,
                SiteId = "MLA",
                ListingTypeId = "gold_special",
                Price = 129.99m,
                CurrencyId = "BRL"
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ListingDraftResult>();
        Assert.NotNull(payload);
        Assert.Equal("MLA1055", payload!.CategoryId);
    }

    [Fact]
    public async Task Upsert_DoesNotOverrideExplicitCategory_WhenAutofillExists()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-suggest-03";
        const string tenantSlug = "tenantdraftsuggest03";
        var clientId = Guid.NewGuid();
        const long sellerId = 1010202;
        const string baseSku = "SKU-BASE-DR-SUG-03";
        const string variantSku = "SKU-VAR-DR-SUG-03";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductVariantAsync(baseSku, variantSku, includeImage: true, categorySlug: "ml-test-slug");
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/upsert",
            new ListingDraftUpsertRequest
            {
                IntegrationId = integrationId,
                SabrVariantSku = variantSku,
                CategoryId = "MLB9999",
                ListingTypeId = "gold_special",
                Price = 129.99m,
                CurrencyId = "BRL"
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ListingDraftResult>();
        Assert.NotNull(payload);
        Assert.Equal("MLB9999", payload!.CategoryId);
    }

    [Fact]
    public async Task Upsert_DoesNotApplyAutofill_WhenMappedCategoryIsInvalidForSite()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-suggest-04";
        const string tenantSlug = "tenantdraftsuggest04";
        var clientId = Guid.NewGuid();
        const long sellerId = 1010203;
        const string baseSku = "SKU-BASE-DR-SUG-04";
        const string variantSku = "SKU-VAR-DR-SUG-04";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductVariantAsync(baseSku, variantSku, includeImage: true, categorySlug: "ml-invalid-slug");
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/upsert",
            new ListingDraftUpsertRequest
            {
                IntegrationId = integrationId,
                SabrVariantSku = variantSku,
                SiteId = "MLA",
                ListingTypeId = "gold_special",
                Price = 129.99m,
                CurrencyId = "BRL"
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ListingDraftResult>();
        Assert.NotNull(payload);
        Assert.True(string.IsNullOrWhiteSpace(payload!.CategoryId));
    }

    [Fact]
    public async Task Publish_RequiresRowVersion_Returns422()
    {
        var setup = await SetupPublishDraftAsync("tenant-draft-02", "tenantdraft02", 1010002, "SKU-BASE-DR-02", "SKU-VAR-DR-02");
        using var client = _factory.CreateTenantClient(setup.TenantSlug, setup.TenantId, setup.ClientId);

        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new { draftId = setup.Draft!.DraftId });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal("ROWVERSION_REQUIRED", error!.Code);
    }

    [Fact]
    public async Task Publish_WithoutValidate_Returns422DraftNotValidated()
    {
        var setup = await SetupPublishDraftAsync("tenant-draft-02b", "tenantdraft02b", 1010021, "SKU-BASE-DR-02B", "SKU-VAR-DR-02B", markAsValid: false);
        using var client = _factory.CreateTenantClient(setup.TenantSlug, setup.TenantId, setup.ClientId);

        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new ListingDraftPublishRequest
            {
                DraftId = setup.Draft!.DraftId,
                RowVersion = setup.Draft.RowVersion
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal("DRAFT_NOT_VALIDATED", error!.Code);
    }

    [Fact]
    public async Task Publish_WhenStatusPublishing_Returns409InProgress()
    {
        var setup = await SetupPublishDraftAsync("tenant-draft-03", "tenantdraft03", 1010003, "SKU-BASE-DR-03", "SKU-VAR-DR-03");
        await SetDraftStatusAsync(setup.Draft!.DraftId, ListingDraftStatus.Publishing);
        using var client = _factory.CreateTenantClient(setup.TenantSlug, setup.TenantId, setup.ClientId);

        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new ListingDraftPublishRequest
            {
                DraftId = setup.Draft.DraftId,
                RowVersion = setup.Draft.RowVersion
            });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal("LISTING_PUBLISH_IN_PROGRESS", error!.Code);
    }

    [Fact]
    public async Task Publish_WhenRowVersionMismatch_Returns409ConcurrencyConflict()
    {
        var setup = await SetupPublishDraftAsync("tenant-draft-04", "tenantdraft04", 1010004, "SKU-BASE-DR-04", "SKU-VAR-DR-04");
        await TouchDraftUpdatedAtAsync(setup.Draft!.DraftId);
        using var client = _factory.CreateTenantClient(setup.TenantSlug, setup.TenantId, setup.ClientId);

        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new ListingDraftPublishRequest
            {
                DraftId = setup.Draft.DraftId,
                RowVersion = setup.Draft.RowVersion
            });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal("DRAFT_CONCURRENCY_CONFLICT", error!.Code);
    }

    [Fact]
    public async Task Publish_WhenAlreadyPublished_ReturnsIdempotentSuccess()
    {
        var setup = await SetupPublishDraftAsync("tenant-draft-05", "tenantdraft05", 1010005, "SKU-BASE-DR-05", "SKU-VAR-DR-05");
        var currentRowVersion = await MarkDraftAsPublishedAsync(setup.Draft!.DraftId, "ITEM-PUB-05", null);
        using var client = _factory.CreateTenantClient(setup.TenantSlug, setup.TenantId, setup.ClientId);

        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new ListingDraftPublishRequest
            {
                DraftId = setup.Draft.DraftId,
                RowVersion = currentRowVersion
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ListingDraftPublishResult>();
        Assert.NotNull(payload);
        Assert.Equal("Published", payload!.Status);
        Assert.Equal("ITEM-PUB-05", payload.PublishedItemId);
    }

    [Fact]
    public async Task Publish_UsesPermalinkFallbackAndApiUrlFallback()
    {
        var setup = await SetupPublishDraftAsync("tenant-draft-06", "tenantdraft06", 1010006, "SKU-BASE-DR-06", "SKU-VAR-DR-06");
        _factory.FakeMercadoLivreApiClient.PublishResultBySku[setup.VariantSku] = new MercadoLivreCreateItemResult
        {
            ItemId = "ITEM-ML-FALLBACK-06",
            VariationId = null,
            Permalink = null,
            ApiUrl = null
        };

        using var client = _factory.CreateTenantClient(setup.TenantSlug, setup.TenantId, setup.ClientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new ListingDraftPublishRequest
            {
                DraftId = setup.Draft!.DraftId,
                RowVersion = setup.Draft.RowVersion
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ListingDraftPublishResult>();
        Assert.NotNull(payload);
        Assert.Equal("https://produto.mercadolivre.com.br/ITEM-ML-FALLBACK-06", payload!.PublishedPermalink);
        Assert.Equal("https://api.mercadolibre.com/items/ITEM-ML-FALLBACK-06", payload.PublishedApiUrl);
    }

    [Fact]
    public async Task Publish_WithoutTitle_Returns422TitleRequired()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-07";
        const string tenantSlug = "tenantdraft07";
        var clientId = Guid.NewGuid();
        const long sellerId = 1010007;
        const string baseSku = "SKU-BASE-DR-07";
        const string variantSku = "SKU-VAR-DR-07";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductVariantAsync(baseSku, variantSku, includeImage: true, productName: "", variantName: "");
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        var draft = await CreateDraftAsync(tenantSlug, tenantId, clientId, integrationId, variantSku);
        Assert.NotNull(draft);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new ListingDraftPublishRequest { DraftId = draft!.DraftId, RowVersion = draft.RowVersion });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal("TITLE_REQUIRED", error!.Code);
    }

    [Fact]
    public async Task Publish_WithoutPictures_Returns422PicturesRequired()
    {
        var setup = await SetupPublishDraftAsync("tenant-draft-08", "tenantdraft08", 1010008, "SKU-BASE-DR-08", "SKU-VAR-DR-08", includeImage: false);
        using var client = _factory.CreateTenantClient(setup.TenantSlug, setup.TenantId, setup.ClientId);

        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new ListingDraftPublishRequest
            {
                DraftId = setup.Draft!.DraftId,
                RowVersion = setup.Draft.RowVersion
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal("PICTURES_REQUIRED", error!.Code);
    }

    [Fact]
    public async Task Publish_UsesBrandFallback_WhenBrandIsRequired()
    {
        var setup = await SetupPublishDraftAsync("tenant-draft-brand-01", "tenantdraftbrand01", 1010901, "SKU-BASE-BRAND-01", "SKU-VAR-BRAND-01");
        _factory.FakeMercadoLivreApiClient.CategoryAttributesResponse = new List<MercadoLivreCategoryAttributeResponse>
        {
            new() { Id = "BRAND", Name = "Marca", Required = true }
        };

        using var client = _factory.CreateTenantClient(setup.TenantSlug, setup.TenantId, setup.ClientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new ListingDraftPublishRequest
            {
                DraftId = setup.Draft!.DraftId,
                RowVersion = setup.Draft.RowVersion
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var publishCall = Assert.Single(_factory.FakeMercadoLivreApiClient.PublishCalls);
        var brandAttribute = Assert.Single(publishCall.Attributes.Where(item =>
            string.Equals(item.Id, "BRAND", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("Marca Teste", brandAttribute.ValueName);
    }

    [Fact]
    public async Task Publish_WithGtin_SendsGtinAndOmitsEmptyReason()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-gtin-01";
        const string tenantSlug = "tenantdraftgtin01";
        var clientId = Guid.NewGuid();
        const long sellerId = 1010902;
        const string baseSku = "SKU-BASE-GTIN-01";
        const string variantSku = "SKU-VAR-GTIN-01";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductVariantAsync(baseSku, variantSku, includeImage: true);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        var draft = await CreateDraftAsync(
            tenantSlug,
            tenantId,
            clientId,
            integrationId,
            variantSku,
            sellerId,
            new ListingDraftUpsertRequest
            {
                IntegrationId = integrationId,
                SellerId = sellerId.ToString(),
                SabrVariantSku = variantSku,
                CategoryId = "MLB1055",
                ListingTypeId = "gold_special",
                Price = 129.99m,
                CurrencyId = "BRL",
                Gtin = "7891234567890",
                EmptyGtinReason = ""
            });
        Assert.NotNull(draft);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new ListingDraftPublishRequest
            {
                DraftId = draft!.DraftId,
                RowVersion = draft.RowVersion
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var publishCall = Assert.Single(_factory.FakeMercadoLivreApiClient.PublishCalls);
        var gtinAttribute = Assert.Single(publishCall.Attributes.Where(item =>
            string.Equals(item.Id, "GTIN", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("7891234567890", gtinAttribute.ValueName);
        Assert.DoesNotContain(publishCall.Attributes, item =>
            string.Equals(item.Id, "EMPTY_GTIN_REASON", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Publish_WithoutGtin_SendsEmptyGtinReason()
    {
        var setup = await SetupPublishDraftAsync("tenant-draft-gtin-02", "tenantdraftgtin02", 1010903, "SKU-BASE-GTIN-02", "SKU-VAR-GTIN-02");

        using var client = _factory.CreateTenantClient(setup.TenantSlug, setup.TenantId, setup.ClientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new ListingDraftPublishRequest
            {
                DraftId = setup.Draft!.DraftId,
                RowVersion = setup.Draft.RowVersion
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var publishCall = Assert.Single(_factory.FakeMercadoLivreApiClient.PublishCalls);
        var reasonAttribute = Assert.Single(publishCall.Attributes.Where(item =>
            string.Equals(item.Id, "EMPTY_GTIN_REASON", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("NOT_APPLICABLE", reasonAttribute.ValueName);
        Assert.DoesNotContain(publishCall.Attributes, item =>
            string.Equals(item.Id, "GTIN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Publish_WhenMlRejectsPayload_Returns422MlPublishInputInvalid()
    {
        var setup = await SetupPublishDraftAsync("tenant-draft-publish-422", "tenantdraftpublish422", 1010904, "SKU-BASE-PUB-422", "SKU-VAR-PUB-422");
        _factory.FakeMercadoLivreApiClient.CreateItemException = new MercadoLivreApiException(
            HttpStatusCode.UnprocessableEntity,
            "item.invalid",
            "invalid item");

        using var client = _factory.CreateTenantClient(setup.TenantSlug, setup.TenantId, setup.ClientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new ListingDraftPublishRequest
            {
                DraftId = setup.Draft!.DraftId,
                RowVersion = setup.Draft.RowVersion
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("ML_PUBLISH_INPUT_INVALID", payload!.Code);
    }

    [Fact]
    public async Task Publish_WhenMlAuthInvalid_Returns401MlAuthInvalid()
    {
        var setup = await SetupPublishDraftAsync("tenant-draft-publish-401", "tenantdraftpublish401", 1010905, "SKU-BASE-PUB-401", "SKU-VAR-PUB-401");
        _factory.FakeMercadoLivreApiClient.CreateItemException = new MercadoLivreApiException(
            HttpStatusCode.Unauthorized,
            "invalid_token",
            "invalid token");

        using var client = _factory.CreateTenantClient(setup.TenantSlug, setup.TenantId, setup.ClientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new ListingDraftPublishRequest
            {
                DraftId = setup.Draft!.DraftId,
                RowVersion = setup.Draft.RowVersion
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("ML_AUTH_INVALID", payload!.Code);
    }

    [Fact]
    public async Task Publish_WhenMlUnavailable_Returns503MlUnavailable()
    {
        var setup = await SetupPublishDraftAsync("tenant-draft-publish-503", "tenantdraftpublish503", 1010906, "SKU-BASE-PUB-503", "SKU-VAR-PUB-503");
        _factory.FakeMercadoLivreApiClient.CreateItemException = new TaskCanceledException("timeout");

        using var client = _factory.CreateTenantClient(setup.TenantSlug, setup.TenantId, setup.ClientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new ListingDraftPublishRequest
            {
                DraftId = setup.Draft!.DraftId,
                RowVersion = setup.Draft.RowVersion
            });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("ML_UNAVAILABLE", payload!.Code);
        Assert.False(string.IsNullOrWhiteSpace(payload.TraceId));
    }

    [Fact]
    public async Task ValidateDraft_WhenMlCategoryIsInvalid_Returns422()
    {
        var setup = await SetupPublishDraftAsync("tenant-draft-validate-422", "tenantdraftvalidate422", 1010907, "SKU-BASE-VAL-422", "SKU-VAR-VAL-422", markAsValid: false);
        _factory.FakeMercadoLivreApiClient.CategoryCapabilityExceptions.Enqueue(new MercadoLivreApiException(
            HttpStatusCode.NotFound,
            "category.not_found",
            "category invalid"));

        using var client = _factory.CreateTenantClient(setup.TenantSlug, setup.TenantId, setup.ClientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/validate",
            new ListingDraftValidateRequest
            {
                DraftId = setup.Draft!.DraftId
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("ML_CATEGORY_INVALID", payload!.Code);
    }

    [Fact]
    public async Task ValidateDraft_AcceptsOnlyDraftId_AndPersistsValidStatusWithNewRowVersion()
    {
        var setup = await SetupPublishDraftAsync("tenant-draft-08b", "tenantdraft08b", 1010081, "SKU-BASE-DR-08B", "SKU-VAR-DR-08B");
        using var client = _factory.CreateTenantClient(setup.TenantSlug, setup.TenantId, setup.ClientId);

        ListingDraft originalDraft;
        using (var beforeScope = _factory.Services.CreateScope())
        {
            var db = beforeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            originalDraft = await db.ListingDrafts.AsNoTracking().SingleAsync(item => item.DraftId == setup.Draft!.DraftId);
        }

        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/validate",
            new ListingDraftValidateRequest { DraftId = setup.Draft!.DraftId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ListingDraftValidateResult>();
        Assert.NotNull(payload);
        Assert.Equal(setup.Draft.DraftId, payload!.DraftId);
        Assert.True(payload.IsValid);
        Assert.Equal("Valid", payload.Status);
        Assert.False(string.IsNullOrWhiteSpace(payload.RowVersion));

        using var afterScope = _factory.Services.CreateScope();
        var verifyDb = afterScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await verifyDb.ListingDrafts.AsNoTracking().SingleAsync(item => item.DraftId == setup.Draft.DraftId);
        Assert.Equal(ListingDraftStatus.Valid, stored.Status);
        Assert.True(stored.UpdatedAt >= originalDraft.UpdatedAt);
    }

    [Fact]
    public async Task ValidateDraft_WithoutGtinAndReason_ReturnsIssue()
    {
        var setup = await SetupPublishDraftAsync("tenant-draft-08c", "tenantdraft08c", 1010083, "SKU-BASE-DR-08C", "SKU-VAR-DR-08C", markAsValid: false);
        using var client = _factory.CreateTenantClient(setup.TenantSlug, setup.TenantId, setup.ClientId);

        var clearFiscalResponse = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/upsert",
            new ListingDraftUpsertRequest
            {
                DraftId = setup.Draft!.DraftId,
                Gtin = "",
                EmptyGtinReason = ""
            });
        Assert.Equal(HttpStatusCode.OK, clearFiscalResponse.StatusCode);

        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/validate",
            new ListingDraftValidateRequest { DraftId = setup.Draft.DraftId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ListingDraftValidateResult>();
        Assert.NotNull(payload);
        Assert.False(payload!.IsValid);
        var issue = Assert.Single(payload.Issues.Where(item => item.Code == "GTIN_OR_REASON_REQUIRED"));
        Assert.Equal("gtin", issue.FieldPath);
        Assert.Equal("fiscal", issue.Step);
    }

    [Fact]
    public async Task Upsert_RebaixaParaDraft_ApenasQuandoMudancaMaterial()
    {
        var setup = await SetupPublishDraftAsync("tenant-draft-material-01", "tenantdraftmaterial01", 1010082, "SKU-BASE-DR-MAT-01", "SKU-VAR-DR-MAT-01");
        using var client = _factory.CreateTenantClient(setup.TenantSlug, setup.TenantId, setup.ClientId);

        var nonMaterialResponse = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/upsert",
            new ListingDraftUpsertRequest
            {
                DraftId = setup.Draft!.DraftId,
                ProductCost = 45.50m
            });
        Assert.Equal(HttpStatusCode.OK, nonMaterialResponse.StatusCode);
        var nonMaterialPayload = await nonMaterialResponse.Content.ReadFromJsonAsync<ListingDraftResult>();
        Assert.NotNull(nonMaterialPayload);
        Assert.Equal("Valid", nonMaterialPayload!.Status);

        var materialResponse = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/upsert",
            new ListingDraftUpsertRequest
            {
                DraftId = setup.Draft.DraftId,
                Title = "Titulo alterado materialmente"
            });
        Assert.Equal(HttpStatusCode.OK, materialResponse.StatusCode);
        var materialPayload = await materialResponse.Content.ReadFromJsonAsync<ListingDraftResult>();
        Assert.NotNull(materialPayload);
        Assert.Equal("Draft", materialPayload!.Status);
    }

    [Fact]
    public async Task Publish_MultiVariation_PartialSuccess_PersistsResults_AndOnlyMapsPublishedVariation()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-multi-01";
        const string tenantSlug = "tenantdraftmulti01";
        var clientId = Guid.NewGuid();
        const long sellerId = 2010001;
        const string baseSku = "SKU-BASE-MULTI-01";
        const string skuA = "SKU-VAR-MULTI-01-A";
        const string skuB = "SKU-VAR-MULTI-01-B";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductVariantAsync(baseSku, skuA, includeImage: true);
        await SeedProductVariantAsync(baseSku, skuB, includeImage: true);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        var draft = await CreateDraftAsync(
            tenantSlug,
            tenantId,
            clientId,
            integrationId,
            skuA,
            sellerId,
            new ListingDraftUpsertRequest
            {
                IntegrationId = integrationId,
                SellerId = sellerId.ToString(),
                SabrVariantSku = skuA,
                CategoryId = "MLB1055",
                ListingTypeId = "gold_special",
                Price = 129.99m,
                CurrencyId = "BRL",
                PublishMode = "MultiVariation",
                SelectedVariantSkus = new List<string> { skuA, skuB },
                Variations = new List<ListingDraftVariationRequest>
                {
                    new()
                    {
                        SabrVariantSku = skuA,
                        Attributes = new List<ListingDraftVariationAttributeRequest>
                        {
                            new() { Id = "COLOR", ValueName = "Azul" }
                        }
                    },
                    new()
                    {
                        SabrVariantSku = skuB,
                        Attributes = new List<ListingDraftVariationAttributeRequest>
                        {
                            new() { Id = "COLOR", ValueName = "Preto" }
                        }
                    }
                }
            });
        Assert.NotNull(draft);

        _factory.FakeMercadoLivreApiClient.PublishMultiResult = new MercadoLivreCreateItemResult
        {
            ItemId = "ITEM-MULTI-01",
            Permalink = "https://produto.mercadolivre.com.br/ITEM-MULTI-01",
            ApiUrl = "/items/ITEM-MULTI-01",
            Variations = new List<MercadoLivreCreateItemVariationResult>
            {
                new() { SabrVariantSku = skuA, VariationId = "VAR-01-A" },
                new() { SabrVariantSku = skuB, VariationId = null }
            }
        };

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new ListingDraftPublishRequest { DraftId = draft!.DraftId, RowVersion = draft.RowVersion });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ListingDraftPublishResult>();
        Assert.NotNull(payload);
        Assert.Equal("Published", payload!.Status);
        Assert.Equal(2, payload.VariationResults.Count);
        Assert.Contains(payload.VariationResults, item => item.SabrVariantSku == skuA && item.Status == "Published" && item.VariationId == "VAR-01-A");
        Assert.Contains(payload.VariationResults, item => item.SabrVariantSku == skuB && item.Status == "Error" && item.ErrorCode == "ML_VARIATION_ID_MISSING");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storedDraft = await db.ListingDrafts.AsNoTracking().SingleAsync(item => item.DraftId == draft.DraftId);
        Assert.Equal(ListingDraftStatus.Published, storedDraft.Status);
        Assert.Contains("lastPublishResults", storedDraft.ProviderDraftJson, StringComparison.Ordinal);
        Assert.Equal(1, await db.TenantMarketplaceListingMaps.CountAsync(item =>
            item.TenantId == tenantId &&
            item.ClientId == clientId &&
            item.MlItemId == "ITEM-MULTI-01"));
        Assert.Equal(1, await db.TenantMarketplaceListingMaps.CountAsync(item =>
            item.TenantId == tenantId &&
            item.ClientId == clientId &&
            item.MlItemId == "ITEM-MULTI-01" &&
            item.MlVariationId == "VAR-01-A" &&
            item.SabrVariantSku == skuA));
    }

    [Fact]
    public async Task Publish_MultiVariation_WhenNoVariationPublished_SetsDraftStatusError()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-multi-02";
        const string tenantSlug = "tenantdraftmulti02";
        var clientId = Guid.NewGuid();
        const long sellerId = 2010002;
        const string baseSku = "SKU-BASE-MULTI-02";
        const string skuA = "SKU-VAR-MULTI-02-A";
        const string skuB = "SKU-VAR-MULTI-02-B";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductVariantAsync(baseSku, skuA, includeImage: true);
        await SeedProductVariantAsync(baseSku, skuB, includeImage: true);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        var draft = await CreateDraftAsync(
            tenantSlug,
            tenantId,
            clientId,
            integrationId,
            skuA,
            sellerId,
            new ListingDraftUpsertRequest
            {
                IntegrationId = integrationId,
                SellerId = sellerId.ToString(),
                SabrVariantSku = skuA,
                CategoryId = "MLB1055",
                ListingTypeId = "gold_special",
                Price = 129.99m,
                CurrencyId = "BRL",
                PublishMode = "MultiVariation",
                SelectedVariantSkus = new List<string> { skuA, skuB }
            });
        Assert.NotNull(draft);

        _factory.FakeMercadoLivreApiClient.PublishMultiResult = new MercadoLivreCreateItemResult
        {
            ItemId = "ITEM-MULTI-02",
            Permalink = "https://produto.mercadolivre.com.br/ITEM-MULTI-02",
            ApiUrl = "/items/ITEM-MULTI-02",
            Variations = new List<MercadoLivreCreateItemVariationResult>()
        };

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new ListingDraftPublishRequest { DraftId = draft!.DraftId, RowVersion = draft.RowVersion });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ListingDraftPublishResult>();
        Assert.NotNull(payload);
        Assert.Equal("Error", payload!.Status);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storedDraft = await db.ListingDrafts.AsNoTracking().SingleAsync(item => item.DraftId == draft.DraftId);
        Assert.Equal(ListingDraftStatus.Error, storedDraft.Status);
    }

    [Fact]
    public async Task Publish_WhenVariationPriceIsProvided_Returns422PricePerVariationNotSupported()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-multi-03";
        const string tenantSlug = "tenantdraftmulti03";
        var clientId = Guid.NewGuid();
        const long sellerId = 2010003;
        const string baseSku = "SKU-BASE-MULTI-03";
        const string skuA = "SKU-VAR-MULTI-03-A";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductVariantAsync(baseSku, skuA, includeImage: true);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        var draft = await CreateDraftAsync(
            tenantSlug,
            tenantId,
            clientId,
            integrationId,
            skuA,
            sellerId,
            new ListingDraftUpsertRequest
            {
                IntegrationId = integrationId,
                SellerId = sellerId.ToString(),
                SabrVariantSku = skuA,
                CategoryId = "MLB1055",
                ListingTypeId = "gold_special",
                Price = 129.99m,
                CurrencyId = "BRL",
                PublishMode = "MultiVariation",
                SelectedVariantSkus = new List<string> { skuA },
                Variations = new List<ListingDraftVariationRequest>
                {
                    new()
                    {
                        SabrVariantSku = skuA,
                        Price = 111.11m
                    }
                }
            });
        Assert.NotNull(draft);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new ListingDraftPublishRequest
            {
                DraftId = draft!.DraftId,
                RowVersion = draft.RowVersion
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal("PRICE_PER_VARIATION_NOT_SUPPORTED", error!.Code);
    }

    [Fact]
    public async Task ValidateDraft_WhenVariationAxisIsNotAllowed_ReturnsIssueWithFieldPath()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-multi-axis-01";
        const string tenantSlug = "tenantdraftmultiaxis01";
        var clientId = Guid.NewGuid();
        const long sellerId = 2010201;
        const string baseSku = "SKU-BASE-MULTI-AXIS-01";
        const string skuA = "SKU-VAR-MULTI-AXIS-01-A";
        const string skuB = "SKU-VAR-MULTI-AXIS-01-B";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductVariantAsync(baseSku, skuA, includeImage: true);
        await SeedProductVariantAsync(baseSku, skuB, includeImage: true);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);

        _factory.FakeMercadoLivreApiClient.CategoryCapabilityResponse = new MercadoLivreCategoryCapabilityResponse
        {
            CategoryName = "Cozinha",
            CategoryPathFromRoot = "Casa > Cozinha > Termicas",
            AllowsVariations = true,
            MaxVariationsAllowed = 10,
            MaxVariationAttributes = 2,
            AllowedVariationAttributes = new List<string> { "COLOR", "SIZE" }
        };
        _factory.FakeMercadoLivreApiClient.CategoryAttributesResponse = new List<MercadoLivreCategoryAttributeResponse>
        {
            new() { Id = "COLOR", Name = "Cor", IsVariation = true },
            new() { Id = "SIZE", Name = "Tamanho", IsVariation = true }
        };

        var draft = await CreateDraftAsync(
            tenantSlug,
            tenantId,
            clientId,
            integrationId,
            skuA,
            sellerId,
            new ListingDraftUpsertRequest
            {
                IntegrationId = integrationId,
                SellerId = sellerId.ToString(),
                SabrVariantSku = skuA,
                CategoryId = "MLB1055",
                ListingTypeId = "gold_special",
                Price = 129.99m,
                CurrencyId = "BRL",
                PublishMode = "MultiVariation",
                SelectedVariantSkus = new List<string> { skuA, skuB },
                VariationAxes = new List<string> { "Material" }
            });
        Assert.NotNull(draft);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/validate",
            new ListingDraftValidateRequest { DraftId = draft!.DraftId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ListingDraftValidateResult>();
        Assert.NotNull(payload);
        Assert.False(payload!.IsValid);
        var issue = Assert.Single(payload.Issues.Where(item => item.Code == "VARIATION_AXIS_NOT_ALLOWED"));
        Assert.Equal("variationAxes[0]", issue.FieldPath);
        Assert.Contains("Material", issue.Message);
        Assert.Contains("Cor", issue.Message);
        Assert.Contains("Tamanho", issue.Message);
    }

    [Fact]
    public async Task Publish_WhenVariationAxisIsNotAllowed_Returns422()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-multi-axis-02";
        const string tenantSlug = "tenantdraftmultiaxis02";
        var clientId = Guid.NewGuid();
        const long sellerId = 2010202;
        const string baseSku = "SKU-BASE-MULTI-AXIS-02";
        const string skuA = "SKU-VAR-MULTI-AXIS-02-A";
        const string skuB = "SKU-VAR-MULTI-AXIS-02-B";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductVariantAsync(baseSku, skuA, includeImage: true);
        await SeedProductVariantAsync(baseSku, skuB, includeImage: true);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);

        _factory.FakeMercadoLivreApiClient.CategoryCapabilityResponse = new MercadoLivreCategoryCapabilityResponse
        {
            CategoryName = "Cozinha",
            CategoryPathFromRoot = "Casa > Cozinha > Termicas",
            AllowsVariations = true,
            MaxVariationsAllowed = 10,
            MaxVariationAttributes = 2,
            AllowedVariationAttributes = new List<string> { "COLOR", "SIZE" }
        };
        _factory.FakeMercadoLivreApiClient.CategoryAttributesResponse = new List<MercadoLivreCategoryAttributeResponse>
        {
            new() { Id = "COLOR", Name = "Cor", IsVariation = true },
            new() { Id = "SIZE", Name = "Tamanho", IsVariation = true }
        };

        var draft = await CreateDraftAsync(
            tenantSlug,
            tenantId,
            clientId,
            integrationId,
            skuA,
            sellerId,
            new ListingDraftUpsertRequest
            {
                IntegrationId = integrationId,
                SellerId = sellerId.ToString(),
                SabrVariantSku = skuA,
                CategoryId = "MLB1055",
                ListingTypeId = "gold_special",
                Price = 129.99m,
                CurrencyId = "BRL",
                PublishMode = "MultiVariation",
                SelectedVariantSkus = new List<string> { skuA, skuB },
                VariationAxes = new List<string> { "Material" }
            });
        Assert.NotNull(draft);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new ListingDraftPublishRequest { DraftId = draft!.DraftId, RowVersion = draft.RowVersion });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal("VARIATION_AXIS_NOT_ALLOWED", error!.Code);
    }

    [Fact]
    public async Task Publish_MultiVariation_With10Skus_PartialAndStatusRule()
    {
        _factory.FakeMercadoLivreApiClient.CategoryCapabilityResponse = new MercadoLivreCategoryCapabilityResponse
        {
            AllowsVariations = true,
            MaxVariationsAllowed = 20,
            MaxVariationAttributes = 2,
            AllowedVariationAttributes = new List<string> { "COLOR", "SIZE" }
        };
        _factory.FakeMercadoLivreApiClient.CategoryAttributesResponse = new List<MercadoLivreCategoryAttributeResponse>
        {
            new() { Id = "COLOR", Name = "Cor", IsVariation = true },
            new() { Id = "SIZE", Name = "Tamanho", IsVariation = true }
        };

        await _factory.ResetDatabaseAsync();
        const string tenantIdPartial = "tenant-draft-multi-10-01";
        const string tenantSlugPartial = "tenantdraftmulti1001";
        var clientIdPartial = Guid.NewGuid();
        const long sellerIdPartial = 2010301;
        const string baseSkuPartial = "SKU-BASE-MULTI-10-01";
        var skusPartial = Enumerable.Range(1, 10).Select(index => $"SKU-VAR-MULTI-10-01-{index:00}").ToList();

        await SeedTenantClientAsync(tenantIdPartial, tenantSlugPartial, clientIdPartial);
        foreach (var sku in skusPartial)
        {
            await SeedProductVariantAsync(baseSkuPartial, sku, includeImage: true);
        }

        var integrationIdPartial = await SeedConnectionAsync(tenantIdPartial, clientIdPartial, sellerIdPartial);
        var draftPartial = await CreateDraftAsync(
            tenantSlugPartial,
            tenantIdPartial,
            clientIdPartial,
            integrationIdPartial,
            skusPartial[0],
            sellerIdPartial,
            new ListingDraftUpsertRequest
            {
                IntegrationId = integrationIdPartial,
                SellerId = sellerIdPartial.ToString(),
                SabrVariantSku = skusPartial[0],
                CategoryId = "MLB1055",
                ListingTypeId = "gold_special",
                Price = 129.99m,
                CurrencyId = "BRL",
                PublishMode = "MultiVariation",
                SelectedVariantSkus = skusPartial,
                VariationAxes = new List<string> { "Cor" },
                Variations = skusPartial.Select((sku, index) => new ListingDraftVariationRequest
                {
                    SabrVariantSku = sku,
                    Attributes = new List<ListingDraftVariationAttributeRequest>
                    {
                        new() { Id = "COLOR", ValueName = $"Cor {index + 1:00}" }
                    }
                }).ToList()
            });
        Assert.NotNull(draftPartial);

        _factory.FakeMercadoLivreApiClient.PublishMultiResult = new MercadoLivreCreateItemResult
        {
            ItemId = "ITEM-MULTI-10-01",
            Permalink = "https://produto.mercadolivre.com.br/ITEM-MULTI-10-01",
            ApiUrl = "/items/ITEM-MULTI-10-01",
            Variations = skusPartial.Select((sku, index) => new MercadoLivreCreateItemVariationResult
            {
                SabrVariantSku = sku,
                VariationId = index % 2 == 0 ? $"VAR-10-01-{index:00}" : null
            }).ToList()
        };

        using (var partialClient = _factory.CreateTenantClient(tenantSlugPartial, tenantIdPartial, clientIdPartial))
        {
            var partialResponse = await partialClient.PostAsJsonAsync(
                "/api/v1/client/listings/drafts/publish",
                new ListingDraftPublishRequest { DraftId = draftPartial!.DraftId, RowVersion = draftPartial.RowVersion });

            Assert.Equal(HttpStatusCode.OK, partialResponse.StatusCode);
            var partialPayload = await partialResponse.Content.ReadFromJsonAsync<ListingDraftPublishResult>();
            Assert.NotNull(partialPayload);
            Assert.Equal(10, partialPayload!.VariationResults.Count);
            Assert.Equal("Published", partialPayload.Status);
            Assert.Equal(5, partialPayload.VariationResults.Count(item => item.Status == "Published"));
            Assert.Equal(5, partialPayload.VariationResults.Count(item => item.Status == "Error"));
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var storedDraft = await db.ListingDrafts.AsNoTracking().SingleAsync(item => item.DraftId == draftPartial!.DraftId);
            Assert.Equal(ListingDraftStatus.Published, storedDraft.Status);
            using var doc = JsonDocument.Parse(storedDraft.ProviderDraftJson);
            var publishResults = doc.RootElement.GetProperty("lastPublishResults");
            Assert.Equal(10, publishResults.GetArrayLength());

            var mappedSkus = await db.TenantMarketplaceListingMaps.AsNoTracking()
                .Where(item => item.TenantId == tenantIdPartial
                               && item.ClientId == clientIdPartial
                               && item.IntegrationId == integrationIdPartial
                               && item.MlItemId == "ITEM-MULTI-10-01")
                .Select(item => item.SabrVariantSku)
                .ToListAsync();
            Assert.Equal(5, mappedSkus.Count);
            foreach (var sku in mappedSkus)
            {
                Assert.Contains(sku, skusPartial.Where((_, index) => index % 2 == 0));
            }
        }

        await _factory.ResetDatabaseAsync();
        const string tenantIdZero = "tenant-draft-multi-10-02";
        const string tenantSlugZero = "tenantdraftmulti1002";
        var clientIdZero = Guid.NewGuid();
        const long sellerIdZero = 2010302;
        const string baseSkuZero = "SKU-BASE-MULTI-10-02";
        var skusZero = Enumerable.Range(1, 10).Select(index => $"SKU-VAR-MULTI-10-02-{index:00}").ToList();

        await SeedTenantClientAsync(tenantIdZero, tenantSlugZero, clientIdZero);
        foreach (var sku in skusZero)
        {
            await SeedProductVariantAsync(baseSkuZero, sku, includeImage: true);
        }

        var integrationIdZero = await SeedConnectionAsync(tenantIdZero, clientIdZero, sellerIdZero);
        var draftZero = await CreateDraftAsync(
            tenantSlugZero,
            tenantIdZero,
            clientIdZero,
            integrationIdZero,
            skusZero[0],
            sellerIdZero,
            new ListingDraftUpsertRequest
            {
                IntegrationId = integrationIdZero,
                SellerId = sellerIdZero.ToString(),
                SabrVariantSku = skusZero[0],
                CategoryId = "MLB1055",
                ListingTypeId = "gold_special",
                Price = 129.99m,
                CurrencyId = "BRL",
                PublishMode = "MultiVariation",
                SelectedVariantSkus = skusZero,
                VariationAxes = new List<string> { "Cor" }
            });
        Assert.NotNull(draftZero);

        _factory.FakeMercadoLivreApiClient.PublishMultiResult = new MercadoLivreCreateItemResult
        {
            ItemId = "ITEM-MULTI-10-02",
            Permalink = "https://produto.mercadolivre.com.br/ITEM-MULTI-10-02",
            ApiUrl = "/items/ITEM-MULTI-10-02",
            Variations = skusZero.Select(sku => new MercadoLivreCreateItemVariationResult
            {
                SabrVariantSku = sku,
                VariationId = null
            }).ToList()
        };

        using (var zeroClient = _factory.CreateTenantClient(tenantSlugZero, tenantIdZero, clientIdZero))
        {
            var zeroResponse = await zeroClient.PostAsJsonAsync(
                "/api/v1/client/listings/drafts/publish",
                new ListingDraftPublishRequest { DraftId = draftZero!.DraftId, RowVersion = draftZero.RowVersion });

            Assert.Equal(HttpStatusCode.OK, zeroResponse.StatusCode);
            var zeroPayload = await zeroResponse.Content.ReadFromJsonAsync<ListingDraftPublishResult>();
            Assert.NotNull(zeroPayload);
            Assert.Equal(10, zeroPayload!.VariationResults.Count);
            Assert.Equal("Error", zeroPayload.Status);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var storedDraft = await db.ListingDrafts.AsNoTracking().SingleAsync(item => item.DraftId == draftZero!.DraftId);
            Assert.Equal(ListingDraftStatus.Error, storedDraft.Status);
            using var doc = JsonDocument.Parse(storedDraft.ProviderDraftJson);
            var publishResults = doc.RootElement.GetProperty("lastPublishResults");
            Assert.Equal(10, publishResults.GetArrayLength());
            Assert.Equal(0, await db.TenantMarketplaceListingMaps.CountAsync(item =>
                item.TenantId == tenantIdZero &&
                item.ClientId == clientIdZero &&
                item.IntegrationId == integrationIdZero &&
                item.MlItemId == "ITEM-MULTI-10-02"));
        }
    }

    [Fact]
    public async Task CategoryAttributes_ReturnsAllowedAxes()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-cat-01";
        const string tenantSlug = "tenantdraftcat01";
        var clientId = Guid.NewGuid();
        const long sellerId = 2010101;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        _factory.FakeMercadoLivreApiClient.CategoryCapabilityResponse = new MercadoLivreCategoryCapabilityResponse
        {
            CategoryName = "Cozinha",
            CategoryPathFromRoot = "Casa > Cozinha > Termicas",
            AllowsVariations = true,
            MaxVariationsAllowed = 10,
            MaxVariationAttributes = 2,
            AllowedVariationAttributes = new List<string> { "COLOR", "SIZE" }
        };
        _factory.FakeMercadoLivreApiClient.CategoryAttributesResponse = new List<MercadoLivreCategoryAttributeResponse>
        {
            new() { Id = "COLOR", Name = "Cor", IsVariation = true },
            new() { Id = "SIZE", Name = "Tamanho", IsVariation = true },
            new() { Id = "BRAND", Name = "Marca", Required = true }
        };

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/categories/attributes",
            new MarketplaceCategoryAttributesRequest
            {
                IntegrationId = integrationId,
                CategoryId = "MLB1055"
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var payload = JsonSerializer.Deserialize<MarketplaceCategoryAttributesResult>(
            raw,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        Assert.NotNull(payload);
        Assert.Equal("Cozinha", payload!.CategoryName);
        Assert.Equal("Casa > Cozinha > Termicas", payload.CategoryPathFromRoot);
        Assert.Equal(2, payload!.AllowedAxes.Count);
        Assert.Contains("Cor", payload.AllowedAxes);
        Assert.Contains("Tamanho", payload.AllowedAxes);
    }

    [Theory]
    [InlineData("gold_invalid", 120.0, "BRL", "LISTING_TYPE_INVALID")]
    [InlineData("gold_special", 0.0, "BRL", "PRICE_INVALID")]
    [InlineData("gold_pro", 120.0, "USD", "CURRENCY_NOT_SUPPORTED")]
    public async Task Estimate_ValidatesListingTypePriceCurrency(string listingTypeId, decimal price, string currency, string expectedCode)
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-09";
        const string tenantSlug = "tenantdraft09";
        var clientId = Guid.NewGuid();
        const long sellerId = 1010009;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);

        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/fees/estimate",
            new MarketplaceFeesEstimateRequest
            {
                IntegrationId = integrationId,
                CategoryId = "MLB1055",
                ListingTypeId = listingTypeId,
                Price = price,
                CurrencyId = currency
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal(expectedCode, error!.Code);
    }

    [Fact]
    public async Task Publish_WhenStockInvalidForcesZeroAndKeepsPublished()
    {
        var setup = await SetupPublishDraftAsync("tenant-draft-10", "tenantdraft10", 1010010, "SKU-BASE-DR-10", "SKU-VAR-DR-10", physicalStock: 1, reservedStock: 3);
        using var client = _factory.CreateTenantClient(setup.TenantSlug, setup.TenantId, setup.ClientId);

        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new ListingDraftPublishRequest
            {
                DraftId = setup.Draft!.DraftId,
                RowVersion = setup.Draft.RowVersion
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ListingDraftPublishResult>();
        Assert.NotNull(payload);
        Assert.Equal("Published", payload!.Status);
        Assert.Equal(0, payload.EffectiveQuantity);
        Assert.Contains("ML_STOCK_INVALID_FORCED_ZERO", payload.Warnings);
    }

    [Fact]
    public async Task Publish_Failure_TruncatesLastErrorRawJsonAt32Kb()
    {
        var setup = await SetupPublishDraftAsync("tenant-draft-11", "tenantdraft11", 1010011, "SKU-BASE-DR-11", "SKU-VAR-DR-11");
        _factory.FakeMercadoLivreApiClient.CreateItemException = new InvalidOperationException(new string('X', 40000));
        using var client = _factory.CreateTenantClient(setup.TenantSlug, setup.TenantId, setup.ClientId);

        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new ListingDraftPublishRequest
            {
                DraftId = setup.Draft!.DraftId,
                RowVersion = setup.Draft.RowVersion
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal("ML_PUBLISH_FAILED", error!.Code);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.ListingDrafts.SingleAsync(item => item.DraftId == setup.Draft.DraftId);
        Assert.NotNull(stored.LastErrorRawJson);
        Assert.True(stored.LastErrorRawJson!.Length <= 32768);
        Assert.EndsWith("...TRUNCATED", stored.LastErrorRawJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicationsQuery_OrdersByUpdatedCreatedThenDraftIdDesc()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-12";
        const string tenantSlug = "tenantdraft12";
        var clientId = Guid.NewGuid();
        const long sellerId = 1010012;
        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        await SeedProductVariantAsync("SKU-BASE-DR-12", "SKU-VAR-DR-12-A", includeImage: true);
        await SeedProductVariantAsync("SKU-BASE-DR-12", "SKU-VAR-DR-12-B", includeImage: true);
        await SeedProductVariantAsync("SKU-BASE-DR-12", "SKU-VAR-DR-12-C", includeImage: true);

        var tieTime = new DateTimeOffset(2026, 02, 18, 12, 0, 0, TimeSpan.Zero);
        var ids = await SeedQueryDraftsAsync(tenantId, clientId, integrationId, sellerId, tieTime);
        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);

        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/publications/query",
            new ListingPublicationsQueryRequest { Skip = 0, Limit = 20 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ListingPublicationsQueryResult>();
        Assert.NotNull(payload);
        Assert.Equal(3, payload!.Items.Count);

        var expected = ids.OrderByDescending(item => item).ToList();
        Assert.Equal(expected[0], payload.Items[0].DraftId);
        Assert.Equal(expected[1], payload.Items[1].DraftId);
        Assert.Equal(expected[2], payload.Items[2].DraftId);
    }

    [Fact]
    public async Task MyProducts_List_ReturnsMlBadgeSummaryWithLegacyFallback()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-13";
        const string tenantSlug = "tenantdraft13";
        var clientId = Guid.NewGuid();
        const long sellerId = 1010013;
        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        await SeedProductVariantAsync("SKU-BASE-BADGE-13-A", "SKU-VAR-BADGE-13-A", includeImage: true);
        await SeedProductVariantAsync("SKU-BASE-BADGE-13-B", "SKU-VAR-BADGE-13-B", includeImage: true);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.Publications.AddRange(
                new Publication
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    ClientId = clientId,
                    ProductSku = "SKU-BASE-BADGE-13-A",
                    Status = PublicationStatus.Draft,
                    PricingMode = PricingMode.CatalogPrice,
                    CostPriceCentsSnapshot = 1000,
                    CatalogPriceCentsSnapshot = 1500,
                    FinalPriceCentsSnapshot = 1500,
                    CreatedByUserId = clientId,
                    UpdatedByUserId = clientId,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new Publication
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    ClientId = clientId,
                    ProductSku = "SKU-BASE-BADGE-13-B",
                    Status = PublicationStatus.Draft,
                    PricingMode = PricingMode.CatalogPrice,
                    CostPriceCentsSnapshot = 1000,
                    CatalogPriceCentsSnapshot = 1500,
                    FinalPriceCentsSnapshot = 1500,
                    CreatedByUserId = clientId,
                    UpdatedByUserId = clientId,
                    CreatedAt = now.AddMinutes(-1),
                    UpdatedAt = now.AddMinutes(-1)
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
                    BaseProductSku = "SKU-BASE-BADGE-13-A",
                    SabrVariantSku = "SKU-VAR-BADGE-13-A",
                    Status = ListingDraftStatus.Error,
                    CurrencyId = "BRL",
                    ProviderDraftJson = "{}"
                },
                new ListingDraft
                {
                    DraftId = Guid.NewGuid(),
                    TenantId = tenantId,
                    ClientId = clientId,
                    Provider = MarketplaceProvider.MercadoLivre,
                    IntegrationId = integrationId,
                    SellerId = sellerId,
                    BaseProductSku = "SKU-BASE-BADGE-13-A",
                    SabrVariantSku = "SKU-VAR-BADGE-13-A",
                    Status = ListingDraftStatus.Published,
                    CurrencyId = "BRL",
                    ProviderDraftJson = "{}",
                    PublishedItemId = "ITEM-BADGE-13-A"
                });

            db.TenantMarketplaceListingMaps.Add(new TenantMarketplaceListingMap
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                IntegrationId = null,
                SellerId = sellerId,
                MlItemId = "ITEM-BADGE-LEGACY",
                MlVariationId = null,
                SabrVariantSku = "SKU-VAR-BADGE-13-B"
            });

            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.GetAsync("/api/v1/my-products?skip=0&limit=20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<PagedResult<MyProductDraftResult>>();
        Assert.NotNull(payload);
        Assert.Equal(2, payload!.Items.Count);

        var itemA = payload.Items.Single(item => item.ProductSku == "SKU-BASE-BADGE-13-A");
        Assert.Equal("Error", itemA.MlOverallStatus);
        Assert.Equal(1, itemA.MlPublishedCount);
        Assert.Equal(0, itemA.MlDraftCount);
        Assert.Equal(1, itemA.MlErrorCount);

        var itemB = payload.Items.Single(item => item.ProductSku == "SKU-BASE-BADGE-13-B");
        Assert.Equal("Published", itemB.MlOverallStatus);
        Assert.Equal(1, itemB.MlPublishedCount);
        Assert.Equal(0, itemB.MlDraftCount);
        Assert.Equal(0, itemB.MlErrorCount);
    }

    [Fact]
    public async Task Estimate_WithCosts_UsesRoundedCentsAndProfit()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-draft-14";
        const string tenantSlug = "tenantdraft14";
        var clientId = Guid.NewGuid();
        const long sellerId = 1010014;
        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);

        _factory.FakeMercadoLivreApiClient.FeeEstimateResponse = new MercadoLivreFeeEstimateResponse
        {
            SaleFeeAmount = 1.23m,
            FixedFeeAmount = 0.77m,
            TotalFeeAmount = 2.00m,
            RawJson = "{}"
        };

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/fees/estimate",
            new MarketplaceFeesEstimateRequest
            {
                IntegrationId = integrationId,
                SellerId = sellerId.ToString(),
                CategoryId = "MLB1055",
                ListingTypeId = "gold_special",
                Price = 100.015m, // round away from zero => 100.02
                CurrencyId = "BRL",
                ProductCost = 10.004m, // => 10.00
                OperationalCost = 5.006m // => 5.01
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<MarketplaceFeesEstimateResult>();
        Assert.NotNull(payload);
        Assert.Equal(100.02m, payload!.Price);
        Assert.Equal(2.00m, payload.TotalFees);
        Assert.Equal(10.00m, payload.ProductCost);
        Assert.Equal(5.01m, payload.OperationalCost);
        Assert.Equal(83.01m, payload.EstimatedProfit);
    }

    [Fact]
    public async Task Publish_WhenMappingConflict_Returns409AndDoesNotOverwrite()
    {
        var setup = await SetupPublishDraftAsync("tenant-draft-15", "tenantdraft15", 1010015, "SKU-BASE-DR-15", "SKU-VAR-DR-15");
        _factory.FakeMercadoLivreApiClient.PublishResultBySku[setup.VariantSku] = new MercadoLivreCreateItemResult
        {
            ItemId = "ITEM-CONFLICT-15",
            VariationId = null,
            Permalink = "https://produto.mercadolivre.com.br/ITEM-CONFLICT-15",
            ApiUrl = "/items/ITEM-CONFLICT-15"
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TenantMarketplaceListingMaps.Add(new TenantMarketplaceListingMap
            {
                Id = Guid.NewGuid(),
                TenantId = setup.TenantId,
                ClientId = setup.ClientId,
                Provider = MarketplaceProvider.MercadoLivre,
                IntegrationId = setup.IntegrationId,
                SellerId = setup.SellerId,
                MlItemId = "ITEM-CONFLICT-15",
                MlVariationId = null,
                SabrVariantSku = "SKU-OTHER-CONFLICT-15",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateTenantClient(setup.TenantSlug, setup.TenantId, setup.ClientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/publish",
            new ListingDraftPublishRequest
            {
                DraftId = setup.Draft!.DraftId,
                RowVersion = setup.Draft.RowVersion
            });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal("ML_MAPPING_CONFLICT", error!.Code);
    }

    private async Task<PublishSetupContext> SetupPublishDraftAsync(
        string tenantId,
        string tenantSlug,
        long sellerId,
        string baseSku,
        string variantSku,
        bool includeImage = true,
        int physicalStock = 10,
        int reservedStock = 0,
        string? productName = null,
        string? variantName = null,
        bool markAsValid = true)
    {
        await _factory.ResetDatabaseAsync();
        var clientId = Guid.NewGuid();
        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        await SeedProductVariantAsync(
            baseSku,
            variantSku,
            includeImage,
            physicalStock,
            reservedStock,
            productName ?? $"Produto {baseSku}",
            variantName ?? $"Variante {variantSku}");
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        var draft = await CreateDraftAsync(tenantSlug, tenantId, clientId, integrationId, variantSku, sellerId, markAsValid: false);
        if (markAsValid && draft != null)
        {
            draft.RowVersion = await SetDraftStatusAsync(draft.DraftId, ListingDraftStatus.Valid);
        }
        return new PublishSetupContext
        {
            TenantId = tenantId,
            TenantSlug = tenantSlug,
            ClientId = clientId,
            IntegrationId = integrationId,
            SellerId = sellerId,
            BaseSku = baseSku,
            VariantSku = variantSku,
            Draft = draft
        };
    }

    private async Task SeedTenantClientAsync(string tenantId, string tenantSlug, Guid clientId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await db.Tenants.AnyAsync(item => item.Id == tenantId))
        {
            db.Tenants.Add(new Phub.Domain.Entities.Tenant
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

    private async Task SeedProductVariantAsync(
        string baseSku,
        string variantSku,
        bool includeImage,
        int physicalStock = 10,
        int reservedStock = 0,
        string? productName = null,
        string? variantName = null,
        string? categorySlug = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await db.Products.AnyAsync(item => item.Sku == baseSku))
        {
            db.Products.Add(new Product
            {
                Sku = baseSku,
                Name = productName ?? $"Produto {baseSku}",
                Brand = "Marca Teste",
                CategoryId = string.IsNullOrWhiteSpace(categorySlug) ? null : categorySlug.Trim(),
                CostPriceCents = 1000,
                CatalogPriceCents = 1500,
                IsActive = true
            });
        }
        else
        {
            var existing = await db.Products.SingleAsync(item => item.Sku == baseSku);
            existing.Name = productName ?? existing.Name;
            if (categorySlug != null)
            {
                existing.CategoryId = string.IsNullOrWhiteSpace(categorySlug) ? null : categorySlug.Trim();
            }
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (!await db.ProductVariants.AnyAsync(item => item.VariantSku == variantSku))
        {
            db.ProductVariants.Add(new ProductVariant
            {
                VariantSku = variantSku,
                BaseSku = baseSku,
                Name = variantName ?? $"Variante {variantSku}",
                CostPriceCents = 1000,
                CatalogPriceCents = 1500,
                PhysicalStock = physicalStock,
                ReservedStock = reservedStock,
                AvailableStock = Math.Max(0, physicalStock - reservedStock),
                IsActive = true
            });
        }
        else
        {
            var existingVariant = await db.ProductVariants.SingleAsync(item => item.VariantSku == variantSku);
            existingVariant.BaseSku = baseSku;
            existingVariant.Name = variantName ?? existingVariant.Name;
            existingVariant.PhysicalStock = physicalStock;
            existingVariant.ReservedStock = reservedStock;
            existingVariant.AvailableStock = Math.Max(0, physicalStock - reservedStock);
            existingVariant.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (includeImage && !await db.ProductImages.AnyAsync(item => item.ProductSku == baseSku))
        {
            db.ProductImages.Add(new ProductImage
            {
                Id = Guid.NewGuid(),
                ProductSku = baseSku,
                Url = $"https://images.example.test/{baseSku}.jpg",
                MimeType = "image/jpeg",
                SizeBytes = 1024,
                IsPrimary = true,
                SortOrder = 0
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedConnectionAsync(string tenantId, Guid clientId, long sellerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.TenantMarketplaceConnections.FirstOrDefaultAsync(item =>
            item.TenantId == tenantId &&
            item.ClientId == clientId &&
            item.Provider == MarketplaceProvider.MercadoLivre &&
            item.SellerId == sellerId);

        if (existing != null)
        {
            return existing.Id;
        }

        var connection = new TenantMarketplaceConnection
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Provider = MarketplaceProvider.MercadoLivre,
            SellerId = sellerId,
            Nickname = $"seller-{sellerId}",
            AccessToken = "access-token",
            RefreshToken = "refresh-token",
            TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.TenantMarketplaceConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection.Id;
    }

    private async Task<ListingDraftResult?> CreateDraftAsync(
        string tenantSlug,
        string tenantId,
        Guid clientId,
        Guid integrationId,
        string variantSku,
        long? sellerId = null,
        ListingDraftUpsertRequest? customRequest = null,
        bool markAsValid = true)
    {
        var request = customRequest ?? new ListingDraftUpsertRequest();
        request.IntegrationId ??= integrationId;
        request.SellerId ??= sellerId?.ToString();
        request.SabrVariantSku ??= variantSku;
        request.CategoryId ??= "MLB1055";
        request.ListingTypeId ??= "gold_special";
        request.Price ??= 129.99m;
        request.CurrencyId ??= "BRL";
        request.EmptyGtinReason ??= "NOT_APPLICABLE";

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/listings/drafts/upsert",
            request);
        response.EnsureSuccessStatusCode();
        var draft = await response.Content.ReadFromJsonAsync<ListingDraftResult>();
        if (draft != null && markAsValid)
        {
            draft.RowVersion = await SetDraftStatusAsync(draft.DraftId, ListingDraftStatus.Valid);
        }

        return draft;
    }

    private async Task<string> SetDraftStatusAsync(Guid draftId, ListingDraftStatus status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var draft = await db.ListingDrafts.SingleAsync(item => item.DraftId == draftId);
        draft.Status = status;
        draft.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return EncodeFallbackRowVersion(draft.UpdatedAt);
    }

    private async Task TouchDraftUpdatedAtAsync(Guid draftId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var draft = await db.ListingDrafts.SingleAsync(item => item.DraftId == draftId);
        draft.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(2);
        await db.SaveChangesAsync();
    }

    private async Task<string> MarkDraftAsPublishedAsync(Guid draftId, string itemId, string? variationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var draft = await db.ListingDrafts.SingleAsync(item => item.DraftId == draftId);
        draft.Status = ListingDraftStatus.Published;
        draft.PublishedItemId = itemId;
        draft.PublishedVariationId = variationId;
        draft.PublishedPermalink = $"https://produto.mercadolivre.com.br/{itemId}";
        draft.PublishedApiUrl = $"https://api.mercadolibre.com/items/{itemId}";
        draft.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(2);
        await db.SaveChangesAsync();
        return EncodeFallbackRowVersion(draft.UpdatedAt);
    }

    private async Task<List<Guid>> SeedQueryDraftsAsync(
        string tenantId,
        Guid clientId,
        Guid integrationId,
        long sellerId,
        DateTimeOffset tieTime)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = new List<Guid>
        {
            Guid.Parse("00000000-0000-0000-0000-00000000000A"),
            Guid.Parse("00000000-0000-0000-0000-00000000000B"),
            Guid.Parse("00000000-0000-0000-0000-00000000000C")
        };

        db.ListingDrafts.AddRange(
            new ListingDraft
            {
                DraftId = ids[0],
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                IntegrationId = integrationId,
                SellerId = sellerId,
                BaseProductSku = "SKU-BASE-DR-12",
                SabrVariantSku = "SKU-VAR-DR-12-A",
                CategoryId = "MLB1055",
                ListingTypeId = "gold_special",
                PriceCents = 10000,
                CurrencyId = "BRL",
                Status = ListingDraftStatus.Draft,
                ProviderDraftJson = "{}",
                CreatedAt = tieTime,
                UpdatedAt = tieTime
            },
            new ListingDraft
            {
                DraftId = ids[1],
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                IntegrationId = integrationId,
                SellerId = sellerId,
                BaseProductSku = "SKU-BASE-DR-12",
                SabrVariantSku = "SKU-VAR-DR-12-B",
                CategoryId = "MLB1055",
                ListingTypeId = "gold_special",
                PriceCents = 11000,
                CurrencyId = "BRL",
                Status = ListingDraftStatus.Draft,
                ProviderDraftJson = "{}",
                CreatedAt = tieTime,
                UpdatedAt = tieTime
            },
            new ListingDraft
            {
                DraftId = ids[2],
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                IntegrationId = integrationId,
                SellerId = sellerId,
                BaseProductSku = "SKU-BASE-DR-12",
                SabrVariantSku = "SKU-VAR-DR-12-C",
                CategoryId = "MLB1055",
                ListingTypeId = "gold_special",
                PriceCents = 12000,
                CurrencyId = "BRL",
                Status = ListingDraftStatus.Draft,
                ProviderDraftJson = "{}",
                CreatedAt = tieTime,
                UpdatedAt = tieTime
            });

        await db.SaveChangesAsync();
        return ids;
    }

    private static string EncodeFallbackRowVersion(DateTimeOffset updatedAt)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, updatedAt.UtcDateTime.Ticks);
        return Convert.ToBase64String(buffer);
    }

    private sealed class PublishSetupContext
    {
        public string TenantId { get; set; } = string.Empty;
        public string TenantSlug { get; set; } = string.Empty;
        public Guid ClientId { get; set; }
        public Guid IntegrationId { get; set; }
        public long SellerId { get; set; }
        public string BaseSku { get; set; } = string.Empty;
        public string VariantSku { get; set; } = string.Empty;
        public ListingDraftResult? Draft { get; set; }
    }
}
