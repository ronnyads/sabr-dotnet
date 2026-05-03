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

public sealed class MarketplaceResilienceHttpTests : IClassFixture<MercadoLivreTestWebApplicationFactory>
{
    private readonly MercadoLivreTestWebApplicationFactory _factory;

    public MarketplaceResilienceHttpTests(MercadoLivreTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task FeesEstimate_Returns503_WhenMlTransientEvenAfterPreviousSuccess()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-resilience-01";
        const string tenantSlug = "tenantresilience01";
        var clientId = Guid.NewGuid();
        const long sellerId = 2900001;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var request = BuildFeesRequest(integrationId, sellerId, 129.9m);

        var first = await client.PostAsJsonAsync("/api/v1/client/marketplaces/fees/estimate", request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstPayload = await first.Content.ReadFromJsonAsync<MarketplaceFeesEstimateResult>();
        Assert.NotNull(firstPayload);
        Assert.Equal("ml-api", firstPayload!.Source);

        _factory.FakeMercadoLivreApiClient.FeeEstimateExceptions.Enqueue(
            new HttpRequestException("ml unavailable", null, HttpStatusCode.ServiceUnavailable));
        _factory.FakeMercadoLivreApiClient.FeeEstimateExceptions.Enqueue(
            new HttpRequestException("ml unavailable", null, HttpStatusCode.ServiceUnavailable));

        var second = await client.PostAsJsonAsync("/api/v1/client/marketplaces/fees/estimate", request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        var secondPayload = await second.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(secondPayload);
        Assert.Equal("ML_UNAVAILABLE", secondPayload!.Code);
    }

    [Fact]
    public async Task FeesEstimate_Returns503_WhenMlUnavailableAndNoCache()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-resilience-02";
        const string tenantSlug = "tenantresilience02";
        var clientId = Guid.NewGuid();
        const long sellerId = 2900002;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        _factory.FakeMercadoLivreApiClient.FeeEstimateExceptions.Enqueue(
            new HttpRequestException("ml unavailable", null, HttpStatusCode.ServiceUnavailable));
        _factory.FakeMercadoLivreApiClient.FeeEstimateExceptions.Enqueue(
            new HttpRequestException("ml unavailable", null, HttpStatusCode.ServiceUnavailable));

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/fees/estimate",
            BuildFeesRequest(integrationId, sellerId, 88.4m));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("ML_UNAVAILABLE", payload!.Code);
        Assert.False(string.IsNullOrWhiteSpace(payload.TraceId));
    }

    [Fact]
    public async Task FeesEstimate_Returns401_WhenMlAuthInvalid()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-resilience-03";
        const string tenantSlug = "tenantresilience03";
        var clientId = Guid.NewGuid();
        const long sellerId = 2900003;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        var integrationId = await SeedConnectionAsync(
            tenantId,
            clientId,
            sellerId,
            tokenExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            refreshToken: string.Empty);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/fees/estimate",
            BuildFeesRequest(integrationId, sellerId, 88.4m));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("ML_AUTH_INVALID", payload!.Code);
    }

    [Fact]
    public async Task FeesEstimate_Returns422_WhenMlInputInvalid()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-resilience-03b";
        const string tenantSlug = "tenantresilience03b";
        var clientId = Guid.NewGuid();
        const long sellerId = 2900303;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        _factory.FakeMercadoLivreApiClient.FeeEstimateExceptions.Enqueue(
            new MercadoLivreApiException(HttpStatusCode.NotFound, "not_found", "category invalid"));

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/fees/estimate",
            BuildFeesRequest(integrationId, sellerId, 88.4m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("ML_FEES_INPUT_INVALID", payload!.Code);
        Assert.False(string.IsNullOrWhiteSpace(payload.TraceId));
    }

    [Fact]
    public async Task FeesEstimate_Returns422AndSkipsMl_WhenCategoryIdIsLocalInvalidText()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-resilience-03c";
        const string tenantSlug = "tenantresilience03c";
        var clientId = Guid.NewGuid();
        const long sellerId = 2900304;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/fees/estimate",
            BuildFeesRequest(integrationId, sellerId, 88.4m, "cozinha"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("ML_FEES_INPUT_INVALID", payload!.Code);
        Assert.False(string.IsNullOrWhiteSpace(payload.TraceId));
        AssertErrorContainsField(payload, "categoryId");
        Assert.Equal(0, _factory.FakeMercadoLivreApiClient.FeeEstimateCalls);
    }

    [Fact]
    public async Task FeesEstimate_NormalizesLowercaseCategoryId_AndDoesNotFailFormat()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-resilience-03d";
        const string tenantSlug = "tenantresilience03d";
        var clientId = Guid.NewGuid();
        const long sellerId = 2900305;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/fees/estimate",
            BuildFeesRequest(integrationId, sellerId, 88.4m, "mlb1055"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<MarketplaceFeesEstimateResult>();
        Assert.NotNull(payload);
        Assert.Equal("MLB1055", payload!.CategoryId);
        Assert.Equal(1, _factory.FakeMercadoLivreApiClient.FeeEstimateCalls);
    }

    [Theory]
    [InlineData("MLB")]
    [InlineData("MLB12A")]
    public async Task FeesEstimate_Returns422_WhenCategoryIdFormatIsInvalid(string categoryId)
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-resilience-03e";
        const string tenantSlug = "tenantresilience03e";
        var clientId = Guid.NewGuid();
        const long sellerId = 2900306;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/fees/estimate",
            BuildFeesRequest(integrationId, sellerId, 88.4m, categoryId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("ML_FEES_INPUT_INVALID", payload!.Code);
        Assert.False(string.IsNullOrWhiteSpace(payload.TraceId));
        AssertErrorContainsField(payload, "categoryId");
        Assert.Equal(0, _factory.FakeMercadoLivreApiClient.FeeEstimateCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task FeesEstimate_RequiredCategoryBehavior_IsPreserved(string? categoryId)
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-resilience-03f";
        const string tenantSlug = "tenantresilience03f";
        var clientId = Guid.NewGuid();
        const long sellerId = 2900307;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);

        var request = BuildFeesRequest(integrationId, sellerId, 88.4m);
        request.CategoryId = categoryId;

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/fees/estimate",
            request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("CATEGORY_REQUIRED", payload!.Code);
        Assert.Equal(0, _factory.FakeMercadoLivreApiClient.FeeEstimateCalls);
    }

    [Fact]
    public async Task FeesEstimate_ValidatesCategoryPrefixBySiteId()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-resilience-03g";
        const string tenantSlug = "tenantresilience03g";
        var clientId = Guid.NewGuid();
        const long sellerId = 2900308;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);

        var validForMla = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/fees/estimate",
            BuildFeesRequest(integrationId, sellerId, 88.4m, "MLA1055", "MLA"));
        Assert.Equal(HttpStatusCode.OK, validForMla.StatusCode);
        Assert.Equal(1, _factory.FakeMercadoLivreApiClient.FeeEstimateCalls);

        var invalidForMla = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/fees/estimate",
            BuildFeesRequest(integrationId, sellerId, 88.4m, "MLB1055", "MLA"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidForMla.StatusCode);
        var invalidPayload = await invalidForMla.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(invalidPayload);
        Assert.Equal("ML_FEES_INPUT_INVALID", invalidPayload!.Code);
        Assert.False(string.IsNullOrWhiteSpace(invalidPayload.TraceId));
        AssertErrorContainsField(invalidPayload, "categoryId");
        Assert.Equal(1, _factory.FakeMercadoLivreApiClient.FeeEstimateCalls);
    }

    [Fact]
    public async Task FeesEstimate_Returns503_WhenUnhandledExceptionOccurs()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-resilience-04";
        const string tenantSlug = "tenantresilience04";
        var clientId = Guid.NewGuid();
        const long sellerId = 2900004;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        _factory.FakeMercadoLivreApiClient.FeeEstimateExceptions.Enqueue(new Exception("unexpected"));

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/fees/estimate",
            BuildFeesRequest(integrationId, sellerId, 88.4m));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("ML_UNAVAILABLE", payload!.Code);
        Assert.False(string.IsNullOrWhiteSpace(payload.TraceId));
    }

    [Fact]
    public async Task CategoryAttributes_Returns503_WhenMlUnavailable()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-resilience-05";
        const string tenantSlug = "tenantresilience05";
        var clientId = Guid.NewGuid();
        const long sellerId = 2900005;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        _factory.FakeMercadoLivreApiClient.CategoryAttributesExceptions.Enqueue(
            new HttpRequestException("ml unavailable", null, HttpStatusCode.ServiceUnavailable));

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/categories/attributes",
            BuildCategoryAttributesRequest(integrationId, sellerId, "MLB1055"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("ML_UNAVAILABLE", payload!.Code);
        Assert.False(string.IsNullOrWhiteSpace(payload.TraceId));
    }

    [Fact]
    public async Task CategoryAttributes_Returns401_WhenMlAuthInvalid()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-resilience-06";
        const string tenantSlug = "tenantresilience06";
        var clientId = Guid.NewGuid();
        const long sellerId = 2900006;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        var integrationId = await SeedConnectionAsync(
            tenantId,
            clientId,
            sellerId,
            tokenExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            refreshToken: string.Empty);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/categories/attributes",
            BuildCategoryAttributesRequest(integrationId, sellerId, "MLB1055"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("ML_AUTH_INVALID", payload!.Code);
    }

    [Fact]
    public async Task CategoryAttributes_Returns422_WhenMlCategoryInvalid()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-resilience-06b";
        const string tenantSlug = "tenantresilience06b";
        var clientId = Guid.NewGuid();
        const long sellerId = 2900606;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        _factory.FakeMercadoLivreApiClient.CategoryAttributesExceptions.Enqueue(
            new MercadoLivreApiException(HttpStatusCode.NotFound, "not_found", "category invalid"));

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/categories/attributes",
            BuildCategoryAttributesRequest(integrationId, sellerId, "MLB-INVALID"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("ML_CATEGORY_INVALID", payload!.Code);
        Assert.False(string.IsNullOrWhiteSpace(payload.TraceId));
    }

    [Fact]
    public async Task CategoryAttributes_Returns422AndSkipsMl_WhenCategoryIdIsLocalInvalidText()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-resilience-06c";
        const string tenantSlug = "tenantresilience06c";
        var clientId = Guid.NewGuid();
        const long sellerId = 2900607;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/categories/attributes",
            BuildCategoryAttributesRequest(integrationId, sellerId, "cozinha"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("ML_CATEGORY_INVALID", payload!.Code);
        Assert.False(string.IsNullOrWhiteSpace(payload.TraceId));
        AssertErrorContainsField(payload, "categoryId");
        Assert.Equal(0, _factory.FakeMercadoLivreApiClient.CategoryCapabilityCalls);
        Assert.Equal(0, _factory.FakeMercadoLivreApiClient.CategoryAttributesCalls);
    }

    [Fact]
    public async Task CategoryAttributes_Returns503_WhenUnhandledExceptionOccurs()
    {
        await _factory.ResetDatabaseAsync();
        const string tenantId = "tenant-resilience-07";
        const string tenantSlug = "tenantresilience07";
        var clientId = Guid.NewGuid();
        const long sellerId = 2900007;

        await SeedTenantClientAsync(tenantId, tenantSlug, clientId);
        var integrationId = await SeedConnectionAsync(tenantId, clientId, sellerId);
        _factory.FakeMercadoLivreApiClient.CategoryAttributesExceptions.Enqueue(new Exception("unexpected"));

        using var client = _factory.CreateTenantClient(tenantSlug, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            "/api/v1/client/marketplaces/categories/attributes",
            BuildCategoryAttributesRequest(integrationId, sellerId, "MLB1055"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(payload);
        Assert.Equal("ML_UNAVAILABLE", payload!.Code);
        Assert.False(string.IsNullOrWhiteSpace(payload.TraceId));
    }

    private static MarketplaceFeesEstimateRequest BuildFeesRequest(
        Guid integrationId,
        long sellerId,
        decimal price,
        string categoryId = "MLB1055",
        string siteId = "MLB")
    {
        return new MarketplaceFeesEstimateRequest
        {
            IntegrationId = integrationId,
            Channel = "mercadolivre",
            SellerId = sellerId.ToString(),
            SiteId = siteId,
            CategoryId = categoryId,
            ListingTypeId = "gold_special",
            Price = price,
            CurrencyId = "BRL",
            ProductCost = 33m,
            OperationalCost = 2m
        };
    }

    private static MarketplaceCategoryAttributesRequest BuildCategoryAttributesRequest(Guid integrationId, long sellerId, string categoryId)
    {
        return new MarketplaceCategoryAttributesRequest
        {
            IntegrationId = integrationId,
            Channel = "mercadolivre",
            SellerId = sellerId.ToString(),
            SiteId = "MLB",
            CategoryId = categoryId
        };
    }

    private static void AssertErrorContainsField(ApiError error, string expectedField)
    {
        Assert.NotNull(error.Errors);
        var errorsElement = Assert.IsType<JsonElement>(error.Errors);
        Assert.Equal(JsonValueKind.Array, errorsElement.ValueKind);

        var found = false;
        foreach (var item in errorsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!item.TryGetProperty("field", out var fieldElement))
            {
                continue;
            }

            if (string.Equals(fieldElement.GetString(), expectedField, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                break;
            }
        }

        Assert.True(found, $"Expected field '{expectedField}' in ApiError.Errors.");
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
        DateTimeOffset? tokenExpiresAt = null,
        string? refreshToken = "refresh-token")
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
            RefreshToken = refreshToken ?? string.Empty,
            TokenExpiresAt = tokenExpiresAt ?? DateTimeOffset.UtcNow.AddHours(1),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.TenantMarketplaceConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection.Id;
    }
}
