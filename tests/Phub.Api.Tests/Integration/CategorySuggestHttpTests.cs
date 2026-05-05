using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Phub.Api.Tests.TestHost;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Domain.Protheus;
using Phub.Infrastructure.Persistence;

namespace Phub.Api.Tests.Integration;

public sealed class CategorySuggestHttpTests : IClassFixture<MercadoLivreTestWebApplicationFactory>
{
    private readonly MercadoLivreTestWebApplicationFactory _factory;

    public CategorySuggestHttpTests(MercadoLivreTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CategoriesSuggest_ReturnsCandidates_FromDomainDiscovery()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-suggest-01";
        const string tenantSlug = "tenantsuggest01";
        var clientId = Guid.NewGuid();
        const long sellerId = 1900001;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        _ = await SeedConnectionAsync(tenantId, clientId, sellerId);
        _factory.FakeMercadoLivreApiClient.DomainDiscoverySuggestions = new List<MercadoLivreDomainDiscoverySuggestion>
        {
            new()
            {
                CategoryId = "MLB1055",
                CategoryName = "Relogios",
                DomainId = "MLB-WATCHES",
                DomainName = "Relogios",
                Score = 0.91m,
                PathFromRoot = "Acessorios > Relogios"
            }
        };

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/categories/suggest",
            BuildRequest(sellerId, "Relogio minimalista couro"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<MarketplaceCategorySuggestResult>();
        Assert.NotNull(payload);
        Assert.False(payload!.Degraded);
        Assert.Single(payload.Items);
        Assert.Equal("MLB1055", payload.Items[0].CategoryId);
    }

    [Fact]
    public async Task CategoriesSuggest_RejectsInvalidSellerOwnership()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-suggest-02";
        const string tenantSlug = "tenantsuggest02";
        var clientId = Guid.NewGuid();
        const long sellerId = 1900002;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/categories/suggest",
            BuildRequest(sellerId, "Relogio"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("INVALID_SELLER_INTEGRATION", payload!.Code);
    }

    [Fact]
    public async Task CategoriesSuggest_ReturnsDegraded_WhenMlUnavailable()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-suggest-03";
        const string tenantSlug = "tenantsuggest03";
        var clientId = Guid.NewGuid();
        const long sellerId = 1900003;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        _ = await SeedConnectionAsync(tenantId, clientId, sellerId);
        _factory.FakeMercadoLivreApiClient.DomainDiscoveryExceptions.Enqueue(
            new HttpRequestException("ml unavailable", null, HttpStatusCode.ServiceUnavailable));

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/categories/suggest",
            BuildRequest(sellerId, "Sugestao indisponivel"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<MarketplaceCategorySuggestResult>();
        Assert.NotNull(payload);
        Assert.True(payload!.Degraded);
        Assert.Equal(SuggestDegradedReason.ML_UNAVAILABLE, payload.Reason);
        Assert.NotNull(payload.TraceId);
        Assert.Empty(payload.Items);
    }

    [Fact]
    public async Task CategoriesSuggest_ReturnsLockCacheItems_WhenMlUnavailable()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-suggest-03c";
        const string tenantSlug = "tenantsuggest03c";
        var clientId = Guid.NewGuid();
        const long sellerId = 1900032;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        _ = await SeedConnectionAsync(tenantId, clientId, sellerId);
        await SeedCategoryLockAsync(
            tenantId,
            clientId,
            "SKU-LOCK-SUGGEST-01",
            "MLB",
            "MLB18272",
            "Geladeiras Termicas",
            "Esportes e Fitness > Camping > Geladeiras Termicas");

        _factory.FakeMercadoLivreApiClient.DomainDiscoveryExceptions.Enqueue(
            new HttpRequestException("ml unavailable", null, HttpStatusCode.ServiceUnavailable));

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/categories/suggest",
            BuildRequest(sellerId, "Bolsa termica"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<MarketplaceCategorySuggestResult>();
        Assert.NotNull(payload);
        Assert.True(payload!.Degraded);
        Assert.Equal(SuggestDegradedReason.ML_UNAVAILABLE, payload.Reason);
        Assert.NotEmpty(payload.Items);
        Assert.Equal("MLB18272", payload.Items[0].CategoryId);
        Assert.Equal("lock_cache", payload.Items[0].Source);
    }

    [Fact]
    public async Task CategoriesSuggest_ReturnsDegraded_WhenUnhandledExceptionOccurs()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-suggest-03b";
        const string tenantSlug = "tenantsuggest03b";
        var clientId = Guid.NewGuid();
        const long sellerId = 1900031;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        _ = await SeedConnectionAsync(tenantId, clientId, sellerId);
        _factory.FakeMercadoLivreApiClient.DomainDiscoveryExceptions.Enqueue(new Exception("unexpected"));

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/categories/suggest",
            BuildRequest(sellerId, "Sugestao exception"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<MarketplaceCategorySuggestResult>();
        Assert.NotNull(payload);
        Assert.True(payload!.Degraded);
        Assert.Equal(SuggestDegradedReason.ML_UNAVAILABLE, payload.Reason);
        Assert.NotNull(payload.TraceId);
        Assert.Empty(payload.Items);
    }

    [Fact]
    public async Task CategoriesSuggest_ReturnsDegraded_WhenSuggestTimeouts()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-suggest-04";
        const string tenantSlug = "tenantsuggest04";
        var clientId = Guid.NewGuid();
        const long sellerId = 1900004;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        _ = await SeedConnectionAsync(tenantId, clientId, sellerId);
        _factory.FakeMercadoLivreApiClient.DomainDiscoveryExceptions.Enqueue(new TaskCanceledException("timeout"));

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/categories/suggest",
            BuildRequest(sellerId, "Sugestao timeout"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<MarketplaceCategorySuggestResult>();
        Assert.NotNull(payload);
        Assert.True(payload!.Degraded);
        Assert.Equal(SuggestDegradedReason.TIMEOUT, payload.Reason);
        Assert.Empty(payload.Items);
    }

    [Fact]
    public async Task CategoriesSuggest_RetriesOnce_On401_AndReturnsSuggestions()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-suggest-05";
        const string tenantSlug = "tenantsuggest05";
        var clientId = Guid.NewGuid();
        const long sellerId = 1900005;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        _ = await SeedConnectionAsync(tenantId, clientId, sellerId, tokenExpiresAt: DateTimeOffset.UtcNow.AddHours(3));
        _factory.FakeMercadoLivreApiClient.DomainDiscoveryExceptions.Enqueue(
            new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized));
        _factory.FakeMercadoLivreApiClient.DomainDiscoverySuggestions = new List<MercadoLivreDomainDiscoverySuggestion>
        {
            new()
            {
                CategoryId = "MLB2001",
                CategoryName = "Eletronicos",
                Score = 0.8m,
                PathFromRoot = "Eletronicos > Gadgets"
            }
        };

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/categories/suggest",
            BuildRequest(sellerId, "relogio retry 401"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<MarketplaceCategorySuggestResult>();
        Assert.NotNull(payload);
        Assert.False(payload!.Degraded);
        Assert.Single(payload.Items);
        Assert.Equal(2, _factory.FakeMercadoLivreApiClient.DomainDiscoveryCalls);
        Assert.Equal(1, _factory.FakeMercadoLivreApiClient.RefreshTokenCalls);
    }

    [Fact]
    public async Task CategoriesSuggest_ReturnsDegraded_WhenAuthInvalidPersistsAfterRetry()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-suggest-06";
        const string tenantSlug = "tenantsuggest06";
        var clientId = Guid.NewGuid();
        const long sellerId = 1900006;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        _ = await SeedConnectionAsync(tenantId, clientId, sellerId, tokenExpiresAt: DateTimeOffset.UtcNow.AddHours(3));
        _factory.FakeMercadoLivreApiClient.DomainDiscoveryExceptions.Enqueue(
            new HttpRequestException("unauthorized-1", null, HttpStatusCode.Unauthorized));
        _factory.FakeMercadoLivreApiClient.DomainDiscoveryExceptions.Enqueue(
            new HttpRequestException("unauthorized-2", null, HttpStatusCode.Unauthorized));

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/categories/suggest",
            BuildRequest(sellerId, "relogio auth invalid"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<MarketplaceCategorySuggestResult>();
        Assert.NotNull(payload);
        Assert.True(payload!.Degraded);
        Assert.Equal(SuggestDegradedReason.ML_AUTH_INVALID, payload.Reason);
        Assert.Equal(2, _factory.FakeMercadoLivreApiClient.DomainDiscoveryCalls);
        Assert.Equal(1, _factory.FakeMercadoLivreApiClient.RefreshTokenCalls);
    }

    [Fact]
    public async Task CategoriesSuggest_DoesNotCacheDegraded()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-suggest-07";
        const string tenantSlug = "tenantsuggest07";
        var clientId = Guid.NewGuid();
        const long sellerId = 1900007;
        const string query = "query-cache-degraded";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        _ = await SeedConnectionAsync(tenantId, clientId, sellerId);

        _factory.FakeMercadoLivreApiClient.DomainDiscoveryExceptions.Enqueue(
            new HttpRequestException("ml unavailable", null, HttpStatusCode.ServiceUnavailable));

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var first = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/categories/suggest",
            BuildRequest(sellerId, query));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstPayload = await first.Content.ReadFromJsonAsync<MarketplaceCategorySuggestResult>();
        Assert.NotNull(firstPayload);
        Assert.True(firstPayload!.Degraded);
        Assert.Empty(firstPayload.Items);

        _factory.FakeMercadoLivreApiClient.DomainDiscoverySuggestions = new List<MercadoLivreDomainDiscoverySuggestion>
        {
            new()
            {
                CategoryId = "MLB3001",
                CategoryName = "Beleza",
                Score = 0.77m,
                PathFromRoot = "Saude e Beleza > Beleza"
            }
        };

        var second = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/categories/suggest",
            BuildRequest(sellerId, query));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondPayload = await second.Content.ReadFromJsonAsync<MarketplaceCategorySuggestResult>();
        Assert.NotNull(secondPayload);
        Assert.False(secondPayload!.Degraded);
        Assert.Single(secondPayload.Items);
        Assert.Equal(2, _factory.FakeMercadoLivreApiClient.DomainDiscoveryCalls);
    }

    [Fact]
    public async Task CategoriesSuggest_DoesNotCacheEmptyResult()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-suggest-08";
        const string tenantSlug = "tenantsuggest08";
        var clientId = Guid.NewGuid();
        const long sellerId = 1900008;
        const string query = "query-cache-empty";

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        _ = await SeedConnectionAsync(tenantId, clientId, sellerId);
        _factory.FakeMercadoLivreApiClient.DomainDiscoverySuggestions = new List<MercadoLivreDomainDiscoverySuggestion>();

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var first = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/categories/suggest",
            BuildRequest(sellerId, query));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstPayload = await first.Content.ReadFromJsonAsync<MarketplaceCategorySuggestResult>();
        Assert.NotNull(firstPayload);
        Assert.False(firstPayload!.Degraded);
        Assert.Empty(firstPayload.Items);

        _factory.FakeMercadoLivreApiClient.DomainDiscoverySuggestions = new List<MercadoLivreDomainDiscoverySuggestion>
        {
            new()
            {
                CategoryId = "MLB3002",
                CategoryName = "Esporte",
                Score = 0.73m,
                PathFromRoot = "Esporte e Fitness > Acessorios"
            }
        };

        var second = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/categories/suggest",
            BuildRequest(sellerId, query));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondPayload = await second.Content.ReadFromJsonAsync<MarketplaceCategorySuggestResult>();
        Assert.NotNull(secondPayload);
        Assert.False(secondPayload!.Degraded);
        Assert.Single(secondPayload.Items);
        Assert.Equal(2, _factory.FakeMercadoLivreApiClient.DomainDiscoveryCalls);
    }

    [Fact]
    public async Task CategoriesSuggest_CanceledToken_ReturnsDegradedTimeout()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-suggest-09";
        const string tenantSlug = "tenantsuggest09";
        var clientId = Guid.NewGuid();

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ListingDraftService>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await service.SuggestCategoriesAsync(
            tenantId,
            clientId,
            new MarketplaceCategorySuggestRequest
            {
                Channel = "mercadolivre",
                SellerId = "1900009",
                SiteId = "MLB",
                Query = "cancelado"
            },
            cts.Token);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.True(result.Data!.Degraded);
        Assert.Equal(SuggestDegradedReason.TIMEOUT, result.Data.Reason);
    }

    private static MarketplaceCategorySuggestRequest BuildRequest(long sellerId, string query)
    {
        return new MarketplaceCategorySuggestRequest
        {
            Channel = "mercadolivre",
            SellerId = sellerId.ToString(),
            SiteId = "MLB",
            Query = query
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

    private async Task<Guid> SeedConnectionAsync(
        string tenantId,
        Guid clientId,
        long sellerId,
        DateTimeOffset? tokenExpiresAt = null)
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
            TokenExpiresAt = tokenExpiresAt ?? DateTimeOffset.UtcNow.AddHours(1),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.TenantMarketplaceConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection.Id;
    }

    private async Task SeedCategoryLockAsync(
        string tenantId,
        Guid clientId,
        string baseProductSku,
        string siteId,
        string categoryId,
        string categoryName,
        string categoryPath)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ProductMarketplaceCategoryLocks.Add(new ProductMarketplaceCategoryLock
        {
            TenantId = tenantId,
            ClientId = clientId,
            BaseProductSku = baseProductSku,
            SiteId = siteId,
            ApprovedCategoryId = categoryId,
            ApprovedCategoryName = categoryName,
            ApprovedCategoryPath = categoryPath,
            Status = MarketplaceCategoryLockStatus.ApprovedAuto,
            Source = MarketplaceCategoryLockSource.DomainDiscovery,
            InternalCategorySlugSnapshot = "geladeiras-termicas",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
