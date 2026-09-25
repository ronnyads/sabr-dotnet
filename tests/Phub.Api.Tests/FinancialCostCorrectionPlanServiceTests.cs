using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Infrastructure.Persistence;

namespace Phub.Api.Tests;

public sealed class FinancialCostCorrectionPlanServiceTests
{
    [Fact]
    public async Task DryRun_MixedCost_PreservesLotAndCorrectsOnlyAuthorizedCatalogVersion()
    {
        await using var db = CreateDb();
        var fixture = SeedCandidate(db, economicAt: new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero),
            costSource: "MIXED", quantity: 3,
            references: new object[]
            {
                new { source = "PREPURCHASED_LOT", lotId = Guid.NewGuid(), quantity = 1, unitCostCents = 900, catalogPriceVersionId = (Guid?)null, priceOrigin = "PREPURCHASED_LOT" },
                new { source = "GENERAL_STOCK", lotId = (Guid?)null, quantity = 2, unitCostCents = 4200, catalogPriceVersionId = Guid.Empty, priceOrigin = "MASTER_PRODUCT" }
            }, currentCostCents: 9300);
        ReplaceEmptyVersionReference(fixture.Item, fixture.PriceVersion.Id);
        await db.SaveChangesAsync();

        var result = await new FinancialCostCorrectionPlanService(db).DryRunAsync(fixture.TenantId, fixture.ClientId,
            Request(fixture.SellerId, fixture.PriceVersion.Id), Guid.NewGuid());

        var entry = Assert.Single(result.Report.Manifest);
        Assert.Equal(2, entry.CatalogQuantity);
        Assert.Equal(1, entry.PrePurchasedQuantity);
        Assert.Equal(3900, entry.ReplacementCostCents); // lote 900 + 2 x 1500
        Assert.Equal(5400, entry.ProfitImpactCents);
        Assert.Empty(result.Report.Pending);
        Assert.Equal("DRY_RUN", result.Status);
        Assert.Empty(await db.FinancialCorrectionPlanEntries.ToListAsync());
        Assert.Equal(fixture.Entry.Id, fixture.Head.ActiveEntryId);
    }

    [Fact]
    public async Task DryRun_UsesHalfOpenRange_AndHeadVersionChangesPlanHash()
    {
        await using var db = CreateDb();
        var included = SeedCandidate(db, new DateTimeOffset(2026, 9, 1, 3, 0, 0, TimeSpan.Zero),
            "CATALOG_PRICE", 1,
            [new { source = "GENERAL_STOCK", lotId = (Guid?)null, quantity = 1, unitCostCents = 4200, catalogPriceVersionId = Guid.Empty, priceOrigin = "MASTER_PRODUCT" }],
            4200);
        ReplaceEmptyVersionReference(included.Item, included.PriceVersion.Id);
        var excluded = SeedCandidate(db, new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero),
            "CATALOG_PRICE", 1,
            [new { source = "GENERAL_STOCK", lotId = (Guid?)null, quantity = 1, unitCostCents = 4200, catalogPriceVersionId = Guid.Empty, priceOrigin = "MASTER_PRODUCT" }],
            4200, included.TenantId, included.ClientId, included.SellerId, "PH-AM10");
        included.PriceVersion.ValidTo = new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);
        excluded.PriceVersion.ValidFrom = included.PriceVersion.ValidTo.Value;
        excluded.PriceVersion.Version = 2;
        ReplaceEmptyVersionReference(excluded.Item, excluded.PriceVersion.Id);
        await db.SaveChangesAsync();

        var service = new FinancialCostCorrectionPlanService(db);
        var request = Request(included.SellerId, included.PriceVersion.Id, excluded.PriceVersion.Id);
        var first = await service.DryRunAsync(included.TenantId, included.ClientId, request, Guid.NewGuid());
        Assert.Single(first.Report.Manifest);
        Assert.Equal(included.Entry.Id, first.Report.Manifest.Single().ExpectedActiveEntryId);

        included.Head.Version++;
        await db.SaveChangesAsync();
        var second = await service.DryRunAsync(included.TenantId, included.ClientId, request, Guid.NewGuid());
        Assert.NotEqual(first.PlanHash, second.PlanHash);
        Assert.NotEqual(first.PlanId, second.PlanId);
    }

    [Fact]
    public async Task DryRun_VersionOutsideAuthorizedSet_IsPendingAndNeverInManifest()
    {
        await using var db = CreateDb();
        var fixture = SeedCandidate(db, new DateTimeOffset(2026, 9, 12, 3, 0, 0, TimeSpan.Zero),
            "CATALOG_PRICE", 1,
            [new { source = "GENERAL_STOCK", lotId = (Guid?)null, quantity = 1, unitCostCents = 4200, catalogPriceVersionId = Guid.Empty, priceOrigin = "MASTER_PRODUCT" }],
            4200);
        ReplaceEmptyVersionReference(fixture.Item, fixture.PriceVersion.Id);
        await db.SaveChangesAsync();

        var result = await new FinancialCostCorrectionPlanService(db).DryRunAsync(fixture.TenantId, fixture.ClientId,
            Request(fixture.SellerId, Guid.NewGuid()), Guid.NewGuid());

        Assert.Empty(result.Report.Manifest);
        Assert.Equal("PRICE_VERSION_NOT_AUTHORIZED", Assert.Single(result.Report.Pending).Code);
    }

    [Fact]
    public async Task Preparation_StagesManifestWithoutCreatingLedgerEntriesOrMovingHead()
    {
        await using var db = CreateDb();
        var fixture = SeedCandidate(db, new DateTimeOffset(2026, 9, 12, 3, 0, 0, TimeSpan.Zero),
            "CATALOG_PRICE", 1,
            [new { source = "GENERAL_STOCK", lotId = (Guid?)null, quantity = 1, unitCostCents = 4200, catalogPriceVersionId = Guid.Empty, priceOrigin = "MASTER_PRODUCT" }],
            4200);
        ReplaceEmptyVersionReference(fixture.Item, fixture.PriceVersion.Id);
        await db.SaveChangesAsync();
        var service = new FinancialCostCorrectionPlanService(db);
        var dryRun = await service.DryRunAsync(fixture.TenantId, fixture.ClientId,
            Request(fixture.SellerId, fixture.PriceVersion.Id), Guid.NewGuid());
        var command = new FinancialCorrectionPlanCommand { PlanHash = dryRun.PlanHash, Reason = "Relatório validado" };

        await service.ApproveAsync(fixture.TenantId, fixture.ClientId, dryRun.PlanId, command, Guid.NewGuid());
        var prepared = await service.ResumeAsync(fixture.TenantId, fixture.ClientId, dryRun.PlanId, command, Guid.NewGuid());

        Assert.Equal(FinancialCorrectionPlanStatuses.PendingActivation, prepared.Status);
        Assert.Single(await db.MarketplaceFinancialEntries.ToListAsync());
        Assert.Equal(fixture.Entry.Id, (await db.FinancialEconomicHeads.SingleAsync()).ActiveEntryId);
        var staged = await db.FinancialCorrectionPlanEntries.SingleAsync();
        Assert.Equal(FinancialCorrectionEntryStates.PendingActivation, staged.State);
        Assert.Null(staged.ReplacementEntryId);
    }

    [Fact]
    public async Task Activation_WithChangedHead_MarksPlanStaleAndPublishesNothing()
    {
        await using var db = CreateDb();
        var fixture = SeedCandidate(db, new DateTimeOffset(2026, 9, 12, 3, 0, 0, TimeSpan.Zero),
            "CATALOG_PRICE", 1,
            [new { source = "GENERAL_STOCK", lotId = (Guid?)null, quantity = 1, unitCostCents = 4200, catalogPriceVersionId = Guid.Empty, priceOrigin = "MASTER_PRODUCT" }],
            4200);
        ReplaceEmptyVersionReference(fixture.Item, fixture.PriceVersion.Id);
        await db.SaveChangesAsync();
        var service = new FinancialCostCorrectionPlanService(db);
        var dryRun = await service.DryRunAsync(fixture.TenantId, fixture.ClientId,
            Request(fixture.SellerId, fixture.PriceVersion.Id), Guid.NewGuid());
        var command = new FinancialCorrectionPlanCommand { PlanHash = dryRun.PlanHash, Reason = "Relatório validado" };
        await service.ApproveAsync(fixture.TenantId, fixture.ClientId, dryRun.PlanId, command, Guid.NewGuid());
        await service.ResumeAsync(fixture.TenantId, fixture.ClientId, dryRun.PlanId, command, Guid.NewGuid());

        fixture.Head.Version++;
        await db.SaveChangesAsync();
        var result = await service.ActivateAsync(fixture.TenantId, fixture.ClientId, dryRun.PlanId, command, Guid.NewGuid());

        Assert.Equal(FinancialCorrectionPlanStatuses.Stale, result.Status);
        Assert.Single(await db.MarketplaceFinancialEntries.ToListAsync());
        Assert.Equal(fixture.Entry.Id, (await db.FinancialEconomicHeads.SingleAsync()).ActiveEntryId);
        Assert.Equal(FinancialCorrectionEntryStates.Discarded,
            (await db.FinancialCorrectionPlanEntries.SingleAsync()).State);
    }

    private static FinancialCostCorrectionDryRunRequest Request(long sellerId, params Guid[] versions) => new()
    {
        SellerId = sellerId,
        Reason = "Auditoria controlada dos custos históricos dos séruns",
        Skus = [new FinancialCostCorrectionSkuRequest
        {
            Sku = "PH-AM10",
            CorrectUnitCostCents = 1500,
            IncorrectCatalogPriceVersionIds = versions.ToList()
        }]
    };

    private static void ReplaceEmptyVersionReference(MarketplaceOrderItem item, Guid versionId)
    {
        item.CatalogPriceVersionId = versionId;
        item.CostReferencesJson = item.CostReferencesJson.Replace(Guid.Empty.ToString(), versionId.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static Fixture SeedCandidate(AppDbContext db, DateTimeOffset economicAt, string costSource, int quantity,
        object[] references, long currentCostCents, string? tenantId = null, Guid? clientId = null, long sellerId = 2496573592,
        string sku = "PH-AM10")
    {
        tenantId ??= $"tenant-{Guid.NewGuid():N}";
        clientId ??= Guid.NewGuid();
        var order = new MarketplaceOrder
        {
            TenantId = tenantId, ClientId = clientId.Value, SellerId = sellerId, Provider = MarketplaceProvider.MercadoLivre,
            MlOrderId = $"ORDER-{Guid.NewGuid():N}", Status = "paid", PaidAt = economicAt, ImportedAt = economicAt
        };
        var version = new ProductPriceVersion
        {
            ProductSku = sku, VariantSku = sku, PricingMode = ProductPricingModes.Override,
            CatalogPriceCents = 4200, CatalogCostStatus = CatalogCostStatuses.Resolved,
            CatalogPriceOrigin = CatalogPriceOrigins.VariantOverride,
            ValidFrom = economicAt.AddDays(-30), Version = 1, ChangedByUserId = Guid.NewGuid(), Reason = "Versão incorreta comprovada"
        };
        var item = new MarketplaceOrderItem
        {
            MarketplaceOrderId = order.Id, TenantId = tenantId, ClientId = clientId.Value, SellerId = sellerId,
            Provider = MarketplaceProvider.MercadoLivre, MlItemId = $"MLB{Random.Shared.Next(100000, 999999)}",
            SabrVariantSku = sku, Quantity = quantity, CostSource = costSource, EconomicAt = economicAt,
            EconomicAtSource = "PAID_AT", CatalogPriceVersionId = version.Id,
            CostReferencesJson = JsonSerializer.Serialize(references)
        };
        var entry = new MarketplaceFinancialEntry
        {
            TenantId = tenantId, ClientId = clientId.Value, SellerId = sellerId, Provider = MarketplaceProvider.MercadoLivre,
            EntryType = FinancialEntryTypes.ProductCost, Layer = FinancialLayers.Operational,
            Status = FinancialEntryStatuses.Estimated, AmountCents = -currentCostCents, CurrencyId = "BRL",
            EconomicKey = $"PHUB:{clientId}:ORDER:{order.MlOrderId}:ITEM:{item.Id:N}:PRODUCT_COST",
            IdempotencyKey = Guid.NewGuid().ToString("N"), MarketplaceOrderId = order.Id, MarketplaceOrderItemId = item.Id,
            EconomicOccurredAt = economicAt, SourceEndpoint = "test", CanonicalPayloadHash = new string('A', 64)
        };
        var head = new FinancialEconomicHead
        {
            TenantId = tenantId, ClientId = clientId.Value, SellerId = sellerId, Provider = MarketplaceProvider.MercadoLivre,
            EconomicKey = entry.EconomicKey, ActiveEntryId = entry.Id, Version = 1
        };
        db.AddRange(order, item, version, entry, head);
        return new Fixture(tenantId, clientId.Value, sellerId, order, item, version, entry, head);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"financial-correction-{Guid.NewGuid():N}").Options);

    private sealed record Fixture(string TenantId, Guid ClientId, long SellerId, MarketplaceOrder Order,
        MarketplaceOrderItem Item, ProductPriceVersion PriceVersion, MarketplaceFinancialEntry Entry, FinancialEconomicHead Head);
}
