using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Phub.Api.Security;
using Phub.Api.Tests.TestHost;
using Phub.Application.Options;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Infrastructure.Persistence;

namespace Phub.Api.Tests;

public sealed class MercadoPagoBillingProbeTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"results\":[]}", true, null)]
    [InlineData(HttpStatusCode.Forbidden, "{}", false, "MP_BILLING_HTTP_403")]
    [InlineData(HttpStatusCode.Forbidden, "{\"error\":\"ABUSE_PREVENTION_ERROR\"}", false, "MP_BILLING_HTTP_403:ABUSE_PREVENTION_ERROR")]
    [InlineData(HttpStatusCode.OK, "<html>not billing</html>", false, "MP_BILLING_HTTP_200")]
    public async Task ProbeBillingAsync_OnlyVerifiesARealBillingResponse(
        HttpStatusCode status, string body, bool expectedVerified, string? expectedError)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"mp-probe-{Guid.NewGuid():N}").Options);
        var protector = new EphemeralDataProtectionProvider();
        var grant = new MarketplaceOAuthGrant
        {
            TenantId = "tenant-test", ClientId = Guid.NewGuid(), SellerId = 2496573592,
            Provider = MarketplaceProvider.MercadoLivre, AppFamily = "MERCADO_PAGO",
            AccessTokenProtected = protector.CreateProtector("PrometheusHUB.MercadoPagoOAuthGrant.v1").Protect("test-token"),
            TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        db.MarketplaceOAuthGrants.Add(grant);
        db.TenantMarketplaceConnections.Add(new TenantMarketplaceConnection
        {
            TenantId = grant.TenantId,
            ClientId = grant.ClientId,
            Provider = MarketplaceProvider.MercadoLivre,
            SellerId = grant.SellerId,
            AccessToken = "ml-functional-token",
            RefreshToken = "ml-refresh-token",
            TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });
        await db.SaveChangesAsync();
        Uri? requestedUri = null;
        var handler = new StubHandler(request =>
        {
            requestedUri = request.RequestUri;
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        });
        var mlOAuth = new MercadoLivreOAuthService(db, new FakeMercadoLivreApiClient(),
            Microsoft.Extensions.Options.Options.Create(new MercadoLivreOptions()));
        var service = new MercadoPagoOAuthService(db, new StubClientFactory(handler), protector,
            Microsoft.Extensions.Options.Options.Create(new MercadoPagoOptions()), mlOAuth);

        var result = await service.ProbeBillingAsync(grant.TenantId, grant.ClientId, grant.SellerId, CancellationToken.None);

        Assert.Equal(expectedVerified, result.Verified);
        Assert.Equal(expectedError, result.ErrorCode);
        Assert.Equal("api.mercadolibre.com", requestedUri?.Host);
        Assert.Contains("group=MP", requestedUri?.Query);
        Assert.Contains("document_type=BILL", requestedUri?.Query);
        Assert.Equal("Bearer", handler.LastAuthorizationScheme);
        Assert.Equal("ml-functional-token", handler.LastAuthorizationParameter);
        Assert.Equal(expectedVerified, grant.CapabilitiesJson.Contains("\"billingMercadoPago\":true"));
    }

    [Fact]
    public async Task ProbeBillingAsync_RateLimitUsesDurableCooldownWithoutRepeatedRequests()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"mp-cooldown-{Guid.NewGuid():N}").Options);
        var protector = new EphemeralDataProtectionProvider();
        var grant = new MarketplaceOAuthGrant
        {
            TenantId = "tenant-test", ClientId = Guid.NewGuid(), SellerId = 2496573592,
            Provider = MarketplaceProvider.MercadoLivre, AppFamily = "MERCADO_PAGO",
            AccessTokenProtected = protector.CreateProtector("PrometheusHUB.MercadoPagoOAuthGrant.v1").Protect("test-token"),
            TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        db.MarketplaceOAuthGrants.Add(grant);
        db.TenantMarketplaceConnections.Add(new TenantMarketplaceConnection
        {
            TenantId = grant.TenantId, ClientId = grant.ClientId, Provider = grant.Provider,
            SellerId = grant.SellerId, AccessToken = "ml-functional-token",
            RefreshToken = "ml-refresh-token", TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });
        await db.SaveChangesAsync();
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });
        var mlOAuth = new MercadoLivreOAuthService(db, new FakeMercadoLivreApiClient(),
            Microsoft.Extensions.Options.Options.Create(new MercadoLivreOptions()));
        var service = new MercadoPagoOAuthService(db, new StubClientFactory(handler), protector,
            Microsoft.Extensions.Options.Options.Create(new MercadoPagoOptions()), mlOAuth);

        var first = await service.ProbeBillingAsync(grant.TenantId, grant.ClientId, grant.SellerId, CancellationToken.None);
        var second = await service.ProbeBillingAsync(grant.TenantId, grant.ClientId, grant.SellerId, CancellationToken.None);

        Assert.Equal("MP_BILLING_RATE_LIMITED", first.ErrorCode);
        Assert.Equal(first.ErrorCode, second.ErrorCode);
        Assert.Equal(1, calls);
    }

    private sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastAuthorizationScheme { get; private set; }
        public string? LastAuthorizationParameter { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;
            return Task.FromResult(respond(request));
        }
    }
}
