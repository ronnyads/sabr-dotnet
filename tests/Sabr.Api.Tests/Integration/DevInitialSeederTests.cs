using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sabr.Api.Tests.TestHost;
using Sabr.Domain.Enums;
using Sabr.Infrastructure.Persistence;
using Sabr.Infrastructure.Persistence.Seeding;

namespace Sabr.Api.Tests.Integration;

public sealed class DevInitialSeederTests : IClassFixture<TestWebApplicationFactory>
{
    private const string TenantSlug = "sabr";
    private const string PlatformAdminEmail = "admin.sabr.local@example.test";
    private const string TenantOwnerEmail = "owner.sabr.local@example.test";
    private const string ClientEmail = "cliente.sabr.local@example.test";
    private const long SellerId = 1979655640;

    private readonly TestWebApplicationFactory _factory;

    public DevInitialSeederTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Seed_CreatesExpectedGraph_OnEmptyDatabase()
    {
        await _factory.ResetDatabaseAsync();
        await RunSeederAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tenant = await db.Tenants.SingleAsync(item => item.Slug == TenantSlug);
        Assert.Equal(TenantStatus.Active, tenant.Status);

        var client = await db.Clients.SingleAsync(item => item.TenantId == tenant.Id && item.Email == ClientEmail);
        Assert.Equal(ClientStatus.Approved, client.Status);
        Assert.False(client.MustChangePassword);

        Assert.True(await db.PlatformUsers.AnyAsync(item => item.EmailNormalized == PlatformAdminEmail));
        Assert.True(await db.Users.AnyAsync(item => item.TenantId == tenant.Id && item.Email == TenantOwnerEmail));
        Assert.True(await db.Plans.AnyAsync(item => item.TenantId == tenant.Id && item.IsActive));
        Assert.True(await db.Catalogs.AnyAsync(item => item.TenantId == tenant.Id && item.IsActive));
        Assert.True(await db.ClientPlanSubscriptions.AnyAsync(item =>
            item.TenantId == tenant.Id && item.ClientId == client.Id && item.IsActive));

        Assert.True(await db.Products.AnyAsync(item => item.Sku == "SAD13790"));
        Assert.True(await db.ProductVariants.AnyAsync(item => item.BaseSku == "SAD13790"));
        Assert.True(await db.ProductImages.AnyAsync(item => item.ProductSku == "SAD13790"));

        var connection = await db.TenantMarketplaceConnections.SingleAsync(item =>
            item.TenantId == tenant.Id &&
            item.ClientId == client.Id &&
            item.Provider == MarketplaceProvider.MercadoLivre &&
            item.SellerId == SellerId);

        var draftsCount = await db.ListingDrafts.CountAsync(item =>
            item.TenantId == tenant.Id &&
            item.ClientId == client.Id &&
            item.Provider == MarketplaceProvider.MercadoLivre &&
            item.IntegrationId == connection.Id);

        Assert.True(draftsCount >= 3);
        Assert.True(await db.ListingDrafts.AnyAsync(item =>
            item.TenantId == tenant.Id && item.ClientId == client.Id && item.Status == ListingDraftStatus.Valid));
        Assert.True(await db.ListingDrafts.AnyAsync(item =>
            item.TenantId == tenant.Id && item.ClientId == client.Id && item.Status == ListingDraftStatus.Draft));
    }

    [Fact]
    public async Task Seed_IsIdempotent_WhenRunTwice()
    {
        await _factory.ResetDatabaseAsync();
        await RunSeederAsync();
        await RunSeederAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tenant = await db.Tenants.SingleAsync(item => item.Slug == TenantSlug);
        var client = await db.Clients.SingleAsync(item => item.TenantId == tenant.Id && item.Email == ClientEmail);
        var connection = await db.TenantMarketplaceConnections.SingleAsync(item =>
            item.TenantId == tenant.Id &&
            item.ClientId == client.Id &&
            item.Provider == MarketplaceProvider.MercadoLivre &&
            item.SellerId == SellerId);

        var seedProductSkus = new[] { "SAD13790", "SABR-TERMICA-001", "SABR-COOLER-020" };
        var seedVariantSkus = new[] { "SAD13790", "SAD13790-AZUL", "SABR-TERMICA-001-UN", "SABR-COOLER-020-UN" };
        var seedDraftSkus = new[] { "SAD13790", "SAD13790-AZUL", "SABR-TERMICA-001-UN" };

        Assert.Equal(3, await db.Products.CountAsync(item => seedProductSkus.Contains(item.Sku)));
        Assert.Equal(4, await db.ProductVariants.CountAsync(item => seedVariantSkus.Contains(item.VariantSku)));
        Assert.Equal(3, await db.ListingDrafts.CountAsync(item =>
            item.TenantId == tenant.Id &&
            item.ClientId == client.Id &&
            item.Provider == MarketplaceProvider.MercadoLivre &&
            item.IntegrationId == connection.Id &&
            seedDraftSkus.Contains(item.SabrVariantSku)));
    }

    [Fact]
    public async Task Seed_MlConnection_IsNoTokenMode()
    {
        await _factory.ResetDatabaseAsync();
        await RunSeederAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenant = await db.Tenants.SingleAsync(item => item.Slug == TenantSlug);
        var client = await db.Clients.SingleAsync(item => item.TenantId == tenant.Id && item.Email == ClientEmail);

        var connection = await db.TenantMarketplaceConnections.SingleAsync(item =>
            item.TenantId == tenant.Id &&
            item.ClientId == client.Id &&
            item.Provider == MarketplaceProvider.MercadoLivre &&
            item.SellerId == SellerId);

        Assert.Equal(string.Empty, connection.AccessToken);
        Assert.Equal(string.Empty, connection.RefreshToken);
        Assert.True(connection.TokenExpiresAt < DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Seed_CreatesWizardDrafts_WithExpectedStatuses()
    {
        await _factory.ResetDatabaseAsync();
        await RunSeederAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenant = await db.Tenants.SingleAsync(item => item.Slug == TenantSlug);
        var client = await db.Clients.SingleAsync(item => item.TenantId == tenant.Id && item.Email == ClientEmail);

        var drafts = await db.ListingDrafts
            .Where(item => item.TenantId == tenant.Id && item.ClientId == client.Id)
            .ToListAsync();

        Assert.Contains(drafts, item => item.SabrVariantSku == "SAD13790" && item.Status == ListingDraftStatus.Valid);
        Assert.Contains(drafts, item => item.SabrVariantSku == "SAD13790-AZUL" && item.Status == ListingDraftStatus.Draft);
        Assert.Contains(drafts, item => item.SabrVariantSku == "SABR-TERMICA-001-UN" && item.Status == ListingDraftStatus.Error);
    }

    private async Task RunSeederAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<DevInitialSeeder>();
        await seeder.SeedAsync();
    }
}
