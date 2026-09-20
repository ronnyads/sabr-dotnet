using Microsoft.EntityFrameworkCore;
using Phub.Api.Tests.TestHost;
using Phub.Application.Abstractions;
using Phub.Application.Options;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Infrastructure.Persistence;

namespace Phub.Api.Tests;

public sealed class FinancialCapabilityServiceTests
{
    [Fact]
    public async Task BillingMl_IsNotInferredFromConnectionAndPersistsOnlyAfterRealProbe()
    {
        await using var db = NewDb();
        var connection = NewConnection();
        db.TenantMarketplaceConnections.Add(connection);
        await db.SaveChangesAsync();
        var api = new FakeMercadoLivreApiClient();
        api.UserMeResponse.SellerId = connection.SellerId.ToString();
        var service = NewService(db, api);

        var before = (await service.GetAsync(connection.TenantId, connection.ClientId, false, default)).Single();
        Assert.False(before.BillingMercadoLivre);
        Assert.Contains("BILLING_ML_GRANT_REQUIRED", before.Pending);

        api.BillingProbeResponse = new FinancialBillingProbeResponse(true, false, null);
        var probed = (await service.GetAsync(connection.TenantId, connection.ClientId, true, default)).Single();
        Assert.True(probed.BillingMercadoLivre);
        var after = (await service.GetAsync(connection.TenantId, connection.ClientId, false, default)).Single();
        Assert.True(after.BillingMercadoLivre);
        Assert.Single(db.MarketplaceOAuthGrants.Where(x => x.AppFamily == "MERCADO_LIVRE"));
    }

    [Fact]
    public async Task BillingMl_TransientFailurePreservesPreviousVerification()
    {
        await using var db = NewDb();
        var connection = NewConnection();
        db.TenantMarketplaceConnections.Add(connection);
        await db.SaveChangesAsync();
        var api = new FakeMercadoLivreApiClient();
        api.UserMeResponse.SellerId = connection.SellerId.ToString();
        var service = NewService(db, api);
        api.BillingProbeResponse = new FinancialBillingProbeResponse(true, false, null);
        await service.GetAsync(connection.TenantId, connection.ClientId, true, default);

        api.BillingProbeResponse = new FinancialBillingProbeResponse(false, true, "ML_BILLING_RATE_LIMITED");
        var result = (await service.GetAsync(connection.TenantId, connection.ClientId, true, default)).Single();
        Assert.True(result.BillingMercadoLivre);
        Assert.Contains("ML_BILLING_RATE_LIMITED", result.Pending);
    }

    [Fact]
    public async Task BillingMl_ForbiddenResponseRevokesPreviousVerification()
    {
        await using var db = NewDb();
        var connection = NewConnection();
        db.TenantMarketplaceConnections.Add(connection);
        await db.SaveChangesAsync();
        var api = new FakeMercadoLivreApiClient();
        api.UserMeResponse.SellerId = connection.SellerId.ToString();
        var service = NewService(db, api);
        api.BillingProbeResponse = new FinancialBillingProbeResponse(true, false, null);
        await service.GetAsync(connection.TenantId, connection.ClientId, true, default);

        api.BillingProbeResponse = new FinancialBillingProbeResponse(false, false, "ML_BILLING_HTTP_403");
        var result = (await service.GetAsync(connection.TenantId, connection.ClientId, true, default)).Single();
        Assert.False(result.BillingMercadoLivre);
        Assert.Contains("BILLING_ML_GRANT_REQUIRED", result.Pending);
    }

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"financial-capabilities-{Guid.NewGuid():N}").Options);

    private static TenantMarketplaceConnection NewConnection() => new()
    {
        TenantId = "tenant-test", ClientId = Guid.NewGuid(), SellerId = 1000001,
        AccessToken = "test-token", RefreshToken = "test-refresh", TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    };

    private static FinancialCapabilityService NewService(AppDbContext db, FakeMercadoLivreApiClient api)
    {
        var oauth = new MercadoLivreOAuthService(db, api,
            Microsoft.Extensions.Options.Options.Create(new MercadoLivreOptions()));
        return new FinancialCapabilityService(db, oauth, api);
    }
}
