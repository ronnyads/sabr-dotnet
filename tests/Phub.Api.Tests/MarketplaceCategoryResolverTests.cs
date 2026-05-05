using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Phub.Api.Tests.TestHost;
using Phub.Application.Categories;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Infrastructure.Persistence;

namespace Phub.Api.Tests;

public sealed class MarketplaceCategoryResolverTests
{
    [Fact]
    public async Task Resolve_WithOnlyMappingAndNoDomainCandidates_ReturnsSelectionRequired()
    {
        await using var db = CreateDbContext();
        db.Products.Add(new Product
        {
            Sku = "SKU-CAT-RES-01",
            Name = "Bolsa Termica",
            CategoryId = "geladeiras-termicas",
            Brand = "Teste",
            CostPriceCents = 1000,
            CatalogPriceCents = 1500,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var fakeClient = new FakeMercadoLivreApiClient();
        fakeClient.CategoryCapabilityResponse = new Phub.Application.Models.MercadoLivreCategoryCapabilityResponse
        {
            CategoryName = "Geladeiras Termicas",
            CategoryPathFromRoot = "Esportes e Fitness > Camping > Geladeiras Termicas",
            IsLeaf = true
        };

        var resolver = new MarketplaceCategoryResolver(db, fakeClient, NullLogger<MarketplaceCategoryResolver>.Instance);
        var result = await resolver.ResolveAsync(new MarketplaceCategoryResolverRequest
        {
            TenantId = "tenant-1",
            ClientId = Guid.NewGuid(),
            BaseProductSku = "SKU-CAT-RES-01",
            SiteId = "MLB",
            Query = "bolsa termica",
            AccessToken = "token"
        });

        Assert.Equal(CategoryResolutionStatus.SelectionRequired, result.ResolutionStatus);
        Assert.True(result.CategorySelectionRequired);
        Assert.False(result.HighConfidence);
        Assert.Null(result.SuggestedCategoryId);
        Assert.Equal("SELECTION_REQUIRED_MULTIPLE_MATCHES", result.CategoryResolutionReason);
    }

    [Fact]
    public async Task Resolve_WithSingleDomainCandidate_ReturnsReady()
    {
        await using var db = CreateDbContext();
        db.Products.Add(new Product
        {
            Sku = "SKU-CAT-RES-02",
            Name = "Bolsa Termica 10L",
            Brand = "Teste",
            CostPriceCents = 1000,
            CatalogPriceCents = 1500,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var fakeClient = new FakeMercadoLivreApiClient();
        fakeClient.DomainDiscoverySuggestions = new List<Phub.Application.Models.MercadoLivreDomainDiscoverySuggestion>
        {
            new()
            {
                CategoryId = "MLB18272",
                CategoryName = "Geladeiras Termicas",
                PathFromRoot = "Esportes e Fitness > Camping > Geladeiras Termicas"
            }
        };
        fakeClient.CategoryCapabilityResponse = new Phub.Application.Models.MercadoLivreCategoryCapabilityResponse
        {
            CategoryName = "Geladeiras Termicas",
            CategoryPathFromRoot = "Esportes e Fitness > Camping > Geladeiras Termicas",
            IsLeaf = true
        };

        var resolver = new MarketplaceCategoryResolver(
            db,
            fakeClient,
            NullLogger<MarketplaceCategoryResolver>.Instance);

        var result = await resolver.ResolveAsync(new MarketplaceCategoryResolverRequest
        {
            TenantId = "tenant-2",
            ClientId = Guid.NewGuid(),
            BaseProductSku = "SKU-CAT-RES-02",
            SiteId = "MLB",
            Query = "bolsa termica",
            AccessToken = "token"
        });

        Assert.Equal(CategoryResolutionStatus.Ready, result.ResolutionStatus);
        Assert.True(result.HighConfidence);
        Assert.Equal("MLB18272", result.SuggestedCategoryId);
        Assert.Equal("READY_SINGLE_MATCH", result.CategoryResolutionReason);
    }

    [Fact]
    public async Task Resolve_WithMultipleDomainCandidates_ReturnsSelectionRequired()
    {
        await using var db = CreateDbContext();
        db.Products.Add(new Product
        {
            Sku = "SKU-CAT-RES-03",
            Name = "Produto Teste",
            Brand = "Teste",
            CostPriceCents = 1000,
            CatalogPriceCents = 1500,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var fakeClient = new FakeMercadoLivreApiClient();
        fakeClient.DomainDiscoverySuggestions = new List<Phub.Application.Models.MercadoLivreDomainDiscoverySuggestion>
        {
            new() { CategoryId = "MLB18272", CategoryName = "Geladeiras Termicas" },
            new() { CategoryId = "MLB277912", CategoryName = "Mascaras Led Faciais" }
        };
        fakeClient.CategoryCapabilityResponse = new Phub.Application.Models.MercadoLivreCategoryCapabilityResponse
        {
            CategoryName = "Categoria",
            CategoryPathFromRoot = "Categoria",
            IsLeaf = true
        };

        var resolver = new MarketplaceCategoryResolver(
            db,
            fakeClient,
            NullLogger<MarketplaceCategoryResolver>.Instance);

        var result = await resolver.ResolveAsync(new MarketplaceCategoryResolverRequest
        {
            TenantId = "tenant-3",
            ClientId = Guid.NewGuid(),
            BaseProductSku = "SKU-CAT-RES-03",
            SiteId = "MLB",
            Query = "produto teste",
            AccessToken = "token"
        });

        Assert.Equal(CategoryResolutionStatus.SelectionRequired, result.ResolutionStatus);
        Assert.True(result.CategorySelectionRequired);
        Assert.False(result.HighConfidence);
        Assert.True(result.Suggestions.Count >= 2);
        Assert.Equal("SELECTION_REQUIRED_MULTIPLE_MATCHES", result.CategoryResolutionReason);
    }

    [Fact]
    public async Task Resolve_WithApprovedLockAndCategorySlugChanged_ReturnsReviewRequired()
    {
        await using var db = CreateDbContext();
        var tenantId = "tenant-4";
        var clientId = Guid.NewGuid();
        db.Products.Add(new Product
        {
            Sku = "SKU-CAT-RES-04",
            Name = "Bolsa Termica",
            CategoryId = "geladeiras-termicas",
            Brand = "Teste",
            CostPriceCents = 1000,
            CatalogPriceCents = 1500,
            IsActive = true
        });
        db.ProductMarketplaceCategoryLocks.Add(new ProductMarketplaceCategoryLock
        {
            TenantId = tenantId,
            ClientId = clientId,
            BaseProductSku = "SKU-CAT-RES-04",
            SiteId = "MLB",
            ApprovedCategoryId = "MLB277912",
            ApprovedCategoryName = "Mascaras Led Faciais",
            ApprovedCategoryPath = "Beleza > Tratamentos de Beleza > Mascaras Led Faciais",
            Status = MarketplaceCategoryLockStatus.ApprovedManual,
            Source = MarketplaceCategoryLockSource.Manual,
            InternalCategorySlugSnapshot = "tratamentos-de-beleza"
        });
        await db.SaveChangesAsync();

        var fakeClient = new FakeMercadoLivreApiClient();
        fakeClient.CategoryCapabilityResponse = new Phub.Application.Models.MercadoLivreCategoryCapabilityResponse
        {
            CategoryName = "Mascaras Led Faciais",
            CategoryPathFromRoot = "Beleza > Tratamentos de Beleza > Mascaras Led Faciais",
            IsLeaf = true
        };

        var resolver = new MarketplaceCategoryResolver(
            db,
            fakeClient,
            NullLogger<MarketplaceCategoryResolver>.Instance);

        var result = await resolver.ResolveAsync(new MarketplaceCategoryResolverRequest
        {
            TenantId = tenantId,
            ClientId = clientId,
            BaseProductSku = "SKU-CAT-RES-04",
            SiteId = "MLB",
            Query = "bolsa termica",
            DraftCategoryId = "MLB277912",
            DraftSiteId = "MLB",
            AccessToken = "token"
        });

        Assert.Equal(CategoryResolutionStatus.ReviewRequired, result.ResolutionStatus);
        Assert.True(result.CategorySelectionRequired);
        Assert.True(result.LockRequiresReview);
        Assert.Equal("REVIEW_REQUIRED_STALE_DRAFT", result.CategoryResolutionReason);
    }

    [Fact]
    public async Task Resolve_WithApprovedLockDivergingFromMlSuggestion_ReturnsReviewRequired_EvenWhenSlugSnapshotMatches()
    {
        await using var db = CreateDbContext();
        var tenantId = "tenant-5";
        var clientId = Guid.NewGuid();
        db.Products.Add(new Product
        {
            Sku = "SKU-CAT-RES-05",
            Name = "Bolsa Termica",
            CategoryId = "geladeiras-termicas",
            Brand = "Teste",
            CostPriceCents = 1000,
            CatalogPriceCents = 1500,
            IsActive = true
        });
        db.ProductMarketplaceCategoryLocks.Add(new ProductMarketplaceCategoryLock
        {
            TenantId = tenantId,
            ClientId = clientId,
            BaseProductSku = "SKU-CAT-RES-05",
            SiteId = "MLB",
            ApprovedCategoryId = "MLB277912",
            ApprovedCategoryName = "Mascaras Led Faciais",
            ApprovedCategoryPath = "Beleza > Tratamentos de Beleza > Mascaras Led Faciais",
            Status = MarketplaceCategoryLockStatus.ApprovedManual,
            Source = MarketplaceCategoryLockSource.Manual,
            InternalCategorySlugSnapshot = "geladeiras-termicas"
        });
        await db.SaveChangesAsync();

        var fakeClient = new FakeMercadoLivreApiClient();
        fakeClient.DomainDiscoverySuggestions = new List<Phub.Application.Models.MercadoLivreDomainDiscoverySuggestion>
        {
            new()
            {
                CategoryId = "MLB18272",
                CategoryName = "Geladeiras Termicas",
                PathFromRoot = "Esportes e Fitness > Camping > Geladeiras Termicas"
            }
        };
        fakeClient.CategoryCapabilityResponse = new Phub.Application.Models.MercadoLivreCategoryCapabilityResponse
        {
            CategoryName = "Geladeiras Termicas",
            CategoryPathFromRoot = "Esportes e Fitness > Camping > Geladeiras Termicas",
            IsLeaf = true
        };

        var resolver = new MarketplaceCategoryResolver(
            db,
            fakeClient,
            NullLogger<MarketplaceCategoryResolver>.Instance);

        var result = await resolver.ResolveAsync(new MarketplaceCategoryResolverRequest
        {
            TenantId = tenantId,
            ClientId = clientId,
            BaseProductSku = "SKU-CAT-RES-05",
            SiteId = "MLB",
            Query = "bolsa termica",
            DraftCategoryId = "MLB277912",
            DraftSiteId = "MLB",
            AccessToken = "token"
        });

        Assert.Equal(CategoryResolutionStatus.ReviewRequired, result.ResolutionStatus);
        Assert.Equal("MLB18272", result.SuggestedCategoryId);
        Assert.True(result.LockRequiresReview);
        Assert.Equal("REVIEW_REQUIRED_STALE_DRAFT", result.CategoryResolutionReason);
    }

    [Fact]
    public async Task Resolve_WithoutAccessToken_ReturnsSelectionRequiredMlUnavailable()
    {
        await using var db = CreateDbContext();
        var tenantId = "tenant-6";
        var clientId = Guid.NewGuid();
        db.Products.Add(new Product
        {
            Sku = "SKU-CAT-RES-06",
            Name = "Bolsa Termica 10L",
            Brand = "Teste",
            CostPriceCents = 1000,
            CatalogPriceCents = 1500,
            IsActive = true
        });
        db.ProductMarketplaceCategoryLocks.Add(new ProductMarketplaceCategoryLock
        {
            TenantId = tenantId,
            ClientId = clientId,
            BaseProductSku = "SKU-CAT-RES-06",
            SiteId = "MLB",
            ApprovedCategoryId = "MLB18272",
            ApprovedCategoryName = "Geladeiras Termicas",
            ApprovedCategoryPath = "Esportes e Fitness > Camping > Geladeiras Termicas",
            Status = MarketplaceCategoryLockStatus.ApprovedAuto,
            Source = MarketplaceCategoryLockSource.DomainDiscovery,
            InternalCategorySlugSnapshot = "geladeiras-termicas"
        });
        await db.SaveChangesAsync();

        var fakeClient = new FakeMercadoLivreApiClient();
        var resolver = new MarketplaceCategoryResolver(
            db,
            fakeClient,
            NullLogger<MarketplaceCategoryResolver>.Instance);

        var result = await resolver.ResolveAsync(new MarketplaceCategoryResolverRequest
        {
            TenantId = tenantId,
            ClientId = clientId,
            BaseProductSku = "SKU-CAT-RES-06",
            SiteId = "MLB",
            Query = "bolsa termica",
            AccessToken = null
        });

        Assert.Equal(CategoryResolutionStatus.SelectionRequired, result.ResolutionStatus);
        Assert.True(result.CategorySelectionRequired);
        Assert.Equal("ML_UNAVAILABLE", result.CategoryResolutionReason);
        Assert.Null(result.SuggestedCategoryId);
        Assert.Single(result.Suggestions);
        Assert.Equal("MLB18272", result.Suggestions[0].CategoryId);
        Assert.Equal("lock_cache", result.Suggestions[0].Source);
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new AppDbContext(options);
    }
}
