using Microsoft.EntityFrameworkCore;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Infrastructure.Persistence;

namespace Phub.Api.Tests;

public sealed class CatalogCostDomainTests
{
    [Theory]
    [InlineData(CatalogPriceOrigins.MarketplaceListing)]
    [InlineData(CatalogPriceOrigins.OrderPrice)]
    [InlineData(CatalogPriceOrigins.ChannelPrice)]
    public void CatalogCostPolicy_RejectsChannelRevenueAsCatalogPrice(string origin)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            CatalogCostPolicy.EnsureValid(4_990, CatalogCostStatuses.Resolved, origin));

        Assert.Contains("cannot be used as CatalogPrice", error.Message);
    }

    [Fact]
    public async Task ProductWithoutValidCost_IsPersistedAsCatalogCostPending()
    {
        await using var db = CreateDb();
        var product = new Product
        {
            Sku = "PH-PENDING",
            Name = "Produto importado",
            Brand = "Marca",
            CatalogPriceCents = 0
        };
        db.Products.Add(product);

        await db.SaveChangesAsync();

        Assert.Equal(CatalogCostStatuses.Pending, product.CatalogCostStatus);
        Assert.Equal(CatalogPriceOrigins.None, product.CatalogPriceOrigin);
    }

    [Fact]
    public async Task HistoricalCost_PaidAtTakesPrecedenceOverChannelCreatedAt()
    {
        await using var db = CreateDb();
        var changedAt = new DateTimeOffset(2026, 9, 10, 3, 0, 0, TimeSpan.Zero);
        db.ProductPriceVersions.AddRange(
            Version(1, 1_500, new DateTimeOffset(2026, 9, 1, 3, 0, 0, TimeSpan.Zero), changedAt),
            Version(2, 1_700, changedAt, null));
        await db.SaveChangesAsync();
        var order = Order(
            channelCreatedAt: new DateTimeOffset(2026, 9, 5, 3, 0, 0, TimeSpan.Zero),
            paidAt: new DateTimeOffset(2026, 9, 12, 3, 0, 0, TimeSpan.Zero));

        var result = await new HistoricalProductCostService(db).ResolveAsync(order, Item(order));

        Assert.NotNull(result);
        Assert.Equal(2, result!.Version);
        Assert.Equal(1_700, result.CatalogPriceCents);
        Assert.Equal("PAID_AT", result.EconomicAtSource);
    }

    [Fact]
    public async Task HistoricalCost_WithoutPaidOrChannelDate_RemainsPending()
    {
        await using var db = CreateDb();
        db.ProductPriceVersions.Add(Version(1, 1_500, DateTimeOffset.UtcNow.AddDays(-1), null));
        await db.SaveChangesAsync();
        var order = Order(null, null);

        var result = await new HistoricalProductCostService(db).ResolveAsync(order, Item(order));

        Assert.Null(result);
    }

    [Fact]
    public async Task HistoricalCost_ClientPublicationSnapshot_PrecedesGlobalVariantPrice()
    {
        await using var db = CreateDb();
        var order = Order(
            new DateTimeOffset(2026, 9, 5, 3, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 6, 3, 0, 0, TimeSpan.Zero));
        db.ProductVariants.Add(new ProductVariant
        {
            VariantSku = "PH-SERUM",
            BaseSku = "PH-SERUM",
            Name = "Serum",
            CatalogPriceCents = 3_900,
            CostPriceCents = 0,
            PricingMode = ProductPricingModes.Override,
            CatalogPriceOrigin = CatalogPriceOrigins.VariantOverride
        });
        db.Publications.Add(new Publication
        {
            TenantId = order.TenantId,
            ClientId = order.ClientId,
            ProductSku = "PH-SERUM",
            CatalogPriceCentsSnapshot = 1_500,
            CostPriceCentsSnapshot = 1_500,
            FinalPriceCentsSnapshot = 4_990,
            CreatedByUserId = Guid.NewGuid(),
            UpdatedByUserId = Guid.NewGuid()
        });
        db.ProductPriceVersions.Add(Version(1, 3_900,
            new DateTimeOffset(2026, 9, 1, 3, 0, 0, TimeSpan.Zero), null));
        await db.SaveChangesAsync();

        var result = await new HistoricalProductCostService(db).ResolveAsync(order, Item(order));

        Assert.NotNull(result);
        Assert.Equal(1_500, result!.CatalogPriceCents);
        Assert.Equal(CatalogPriceOrigins.PublicationSnapshot, result.Origin);
        Assert.Equal(Guid.Empty, result.VersionId);
    }

    [Fact]
    public async Task HistoricalCost_WithOverlappingVersions_RemainsPending()
    {
        await using var db = CreateDb();
        var economicAt = new DateTimeOffset(2026, 9, 12, 3, 0, 0, TimeSpan.Zero);
        db.ProductPriceVersions.AddRange(
            Version(1, 1_500, economicAt.AddDays(-10), null),
            Version(2, 1_700, economicAt.AddDays(-2), null));
        await db.SaveChangesAsync();
        var order = Order(economicAt.AddDays(-5), economicAt);

        var result = await new HistoricalProductCostService(db).ResolveAsync(order, Item(order));

        Assert.Null(result);
    }

    [Fact]
    public async Task HistoricalCost_UsesActiveBaselineForOrderImportedAfterApproval()
    {
        await using var db = CreateDb();
        var oldVersion = Version(1, 4_200, new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 20, 3, 0, 0, TimeSpan.Zero));
        var currentVersion = Version(2, 1_500, oldVersion.ValidTo!.Value, null);
        var planId = Guid.NewGuid();
        db.ProductPriceVersions.AddRange(oldVersion, currentVersion);
        db.CatalogCostBaselines.Add(new CatalogCostBaseline
        {
            PlanId = planId,
            ProductSku = "PH-SERUM",
            VariantSku = "PH-SERUM",
            BaselinePriceVersionId = currentVersion.Id,
            BaselineUnitCostCents = 1_500,
            BaselineCutAt = currentVersion.ValidFrom,
            Status = CatalogCostBaselineStatuses.Active,
            ApprovedByUserId = Guid.NewGuid(),
            ApprovedAt = DateTimeOffset.UtcNow,
            ActivatedAt = DateTimeOffset.UtcNow,
            Reason = "Custo revisado",
            PlanHash = new string('B', 64)
        });
        await db.SaveChangesAsync();
        var order = Order(new DateTimeOffset(2026, 9, 5, 3, 0, 0, TimeSpan.Zero), null);

        var result = await new HistoricalProductCostService(db).ResolveAsync(order, Item(order));

        Assert.NotNull(result);
        Assert.Equal(1_500, result!.CatalogPriceCents);
        Assert.Equal(currentVersion.Id, result.VersionId);
        Assert.Equal(CatalogCostBaselineOrigins.ApprovedRetroactiveBaseline, result.Origin);
        Assert.NotNull(result.BaselineId);
    }

    [Fact]
    public async Task HistoricalCost_ActiveBaselineWinsOverLegacyPublicationSnapshot()
    {
        await using var db = CreateDb();
        var currentVersion = Version(2, 1_500,
            new DateTimeOffset(2026, 9, 20, 3, 0, 0, TimeSpan.Zero), null);
        db.ProductPriceVersions.Add(currentVersion);
        var order = Order(new DateTimeOffset(2026, 9, 5, 3, 0, 0, TimeSpan.Zero), null);
        db.Publications.Add(new Publication
        {
            TenantId = order.TenantId,
            ClientId = order.ClientId,
            ProductSku = "PH-SERUM",
            CatalogPriceCentsSnapshot = 4_200,
            CostPriceCentsSnapshot = 4_200,
            FinalPriceCentsSnapshot = 4_990,
            CreatedByUserId = Guid.NewGuid(),
            UpdatedByUserId = Guid.NewGuid()
        });
        db.CatalogCostBaselines.Add(new CatalogCostBaseline
        {
            PlanId = Guid.NewGuid(),
            ProductSku = "PH-SERUM",
            VariantSku = "PH-SERUM",
            BaselinePriceVersionId = currentVersion.Id,
            BaselineUnitCostCents = 1_500,
            BaselineCutAt = currentVersion.ValidFrom,
            Status = CatalogCostBaselineStatuses.Active,
            ApprovedByUserId = Guid.NewGuid(),
            ApprovedAt = DateTimeOffset.UtcNow,
            ActivatedAt = DateTimeOffset.UtcNow,
            Reason = "Custo atual aprovado para histórico",
            PlanHash = new string('C', 64)
        });
        await db.SaveChangesAsync();

        var result = await new HistoricalProductCostService(db).ResolveAsync(order, Item(order));

        Assert.NotNull(result);
        Assert.Equal(1_500, result!.CatalogPriceCents);
        Assert.Equal(CatalogCostBaselineOrigins.ApprovedRetroactiveBaseline, result.Origin);
        Assert.Equal(currentVersion.Id, result.VersionId);
    }

    private static ProductPriceVersion Version(long version, long price, DateTimeOffset from, DateTimeOffset? to) => new()
    {
        ProductSku = "PH-SERUM",
        VariantSku = "PH-SERUM",
        PricingMode = ProductPricingModes.Inherited,
        CatalogPriceCents = price,
        CostPriceCents = price,
        CatalogCostStatus = CatalogCostStatuses.Resolved,
        CatalogPriceOrigin = CatalogPriceOrigins.MasterProduct,
        ValidFrom = from,
        ValidTo = to,
        Version = version,
        ChangedByUserId = Guid.NewGuid(),
        Reason = "Teste"
    };

    private static MarketplaceOrder Order(DateTimeOffset? channelCreatedAt, DateTimeOffset? paidAt) => new()
    {
        TenantId = "tenant",
        ClientId = Guid.NewGuid(),
        Provider = MarketplaceProvider.MercadoLivre,
        SellerId = 10,
        MlOrderId = Guid.NewGuid().ToString("N"),
        Status = "paid",
        ChannelCreatedAt = channelCreatedAt,
        PaidAt = paidAt
    };

    private static MarketplaceOrderItem Item(MarketplaceOrder order) => new()
    {
        MarketplaceOrderId = order.Id,
        TenantId = order.TenantId,
        ClientId = order.ClientId,
        Provider = order.Provider,
        SellerId = order.SellerId,
        MlItemId = "MLB-TEST",
        SabrVariantSku = "PH-SERUM",
        Quantity = 1
    };

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"catalog-cost-domain-{Guid.NewGuid():N}").Options);
}
