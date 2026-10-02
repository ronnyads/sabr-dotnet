using Microsoft.EntityFrameworkCore;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Infrastructure.Persistence;

namespace Phub.Api.Tests;

public sealed class LegacyFinancialCostRepairServiceTests
{
    [Fact]
    public async Task RepairAll_ReplacesLegacySalePriceWithClientCatalogSnapshot()
    {
        await using var db = CreateDb();
        var (order, item, original, head) = SeedLegacyInternal(db, 4_990, 1_500);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var dryRun = await service.RepairAllAsync(false);
        Assert.Equal(1, dryRun.EntriesReplaced);
        Assert.Equal(3_490, dryRun.ProfitImpactCents);
        Assert.Equal(original.Id, (await db.FinancialEconomicHeads.SingleAsync()).ActiveEntryId);

        var applied = await service.RepairAllAsync(true);

        Assert.Equal(1, applied.EntriesReplaced);
        var activeHead = await db.FinancialEconomicHeads.SingleAsync(x => x.Id == head.Id);
        var replacement = await db.MarketplaceFinancialEntries.SingleAsync(x => x.Id == activeHead.ActiveEntryId);
        var repairedItem = await db.MarketplaceOrderItems.SingleAsync(x => x.Id == item.Id);
        Assert.Equal(-1_500, replacement.AmountCents);
        Assert.Equal(original.Id, replacement.SupersedesEntryId);
        Assert.Equal(1_500, repairedItem.CatalogUnitPriceCentsAtPayment);
        Assert.Equal("CATALOG_PRICE", repairedItem.CostSource);
        Assert.Equal(replacement.Id, repairedItem.ProductCostEntryId);
        Assert.NotNull(await db.MarketplaceOrderFinancialStates.SingleOrDefaultAsync(x => x.MarketplaceOrderId == order.Id));

        var retry = await service.RepairAllAsync(true);
        Assert.Equal(0, retry.EntriesReplaced);
    }

    [Fact]
    public async Task RepairAll_VoidsExternalLegacyCostWithoutTrustedSnapshot()
    {
        await using var db = CreateDb();
        var (order, item, original, head) = SeedLegacyInternal(db, 4_990, 1_500);
        item.MappingState = Phub.Application.Models.MarketplaceMappingStates.ExternalSupplier;
        item.ExternalSupplierName = "Parceiro";
        item.SabrVariantSku = null;
        await db.SaveChangesAsync();

        var applied = await CreateService(db).RepairAllAsync(true);

        Assert.Equal(1, applied.ExternalCostsVoided);
        var activeHead = await db.FinancialEconomicHeads.SingleAsync(x => x.Id == head.Id);
        var replacement = await db.MarketplaceFinancialEntries.SingleAsync(x => x.Id == activeHead.ActiveEntryId);
        var repairedItem = await db.MarketplaceOrderItems.SingleAsync(x => x.Id == item.Id);
        Assert.Equal(FinancialEntryStatuses.Voided, replacement.Status);
        Assert.Equal(original.Id, replacement.SupersedesEntryId);
        Assert.Equal(Phub.Application.Models.MarketplaceMappingStates.ExternalCostPending, repairedItem.MappingState);
        Assert.Equal(InternalCostStatuses.Voided, repairedItem.InternalCostStatus);
        var state = await db.MarketplaceOrderFinancialStates.SingleAsync(x => x.MarketplaceOrderId == order.Id);
        Assert.False(state.CostResolved);
        Assert.Contains("EXTERNAL_COST_PENDING", state.IncompleteReasonsJson);
    }

    [Fact]
    public async Task RepairAll_WithScope_DoesNotCrossTenantClientOrSeller()
    {
        await using var db = CreateDb();
        var (order, _, _, _) = SeedLegacyInternal(db, 4_990, 1_500);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var wrongScope = new LegacyFinancialCostRepairScope(order.TenantId, order.ClientId, order.SellerId + 1);
        var excluded = await service.RepairAllAsync(false, scope: wrongScope);
        var correctScope = new LegacyFinancialCostRepairScope(order.TenantId, order.ClientId, order.SellerId);
        var included = await service.RepairAllAsync(false, scope: correctScope);

        Assert.Equal(0, excluded.EntriesReplaced);
        Assert.Equal(1, included.EntriesReplaced);
    }

    private static LegacyFinancialCostRepairService CreateService(AppDbContext db)
    {
        var ledger = new FinancialLedgerService(db);
        var historical = new HistoricalProductCostService(db);
        var projection = new OperationalFinancialProjectionService(db, ledger, historical);
        return new LegacyFinancialCostRepairService(db, projection, historical);
    }

    private static (MarketplaceOrder Order, MarketplaceOrderItem Item,
        MarketplaceFinancialEntry Entry, FinancialEconomicHead Head) SeedLegacyInternal(
        AppDbContext db, long legacyUnitCost, long publicationUnitCost)
    {
        var clientId = Guid.NewGuid();
        var order = new MarketplaceOrder
        {
            TenantId = "tenant",
            ClientId = clientId,
            Provider = MarketplaceProvider.MercadoLivre,
            SellerId = 123,
            MlOrderId = "ORDER-1",
            Status = "paid",
            PaidAt = new DateTimeOffset(2026, 9, 10, 3, 0, 0, TimeSpan.Zero),
            CurrencyId = "BRL"
        };
        var item = new MarketplaceOrderItem
        {
            MarketplaceOrderId = order.Id,
            TenantId = order.TenantId,
            ClientId = clientId,
            Provider = order.Provider,
            SellerId = order.SellerId,
            MlItemId = "MLB-1",
            SabrVariantSku = "PH-TEST",
            Quantity = 1,
            UnitPrice = 49.90m,
            GrossPrice = 49.90m,
            SaleFee = 0m,
            MappingState = Phub.Application.Models.MarketplaceMappingStates.MappedByListingMap
        };
        order.Items.Add(item);
        var entry = new MarketplaceFinancialEntry
        {
            TenantId = order.TenantId,
            ClientId = clientId,
            Provider = order.Provider,
            SellerId = order.SellerId,
            EntryType = FinancialEntryTypes.ProductCost,
            Layer = FinancialLayers.Operational,
            Status = FinancialEntryStatuses.Estimated,
            AmountCents = -legacyUnitCost,
            EconomicKey = $"PHUB:{clientId}:ORDER:{order.MlOrderId}:ITEM:{item.Id:N}:PRODUCT_COST",
            IdempotencyKey = "LEGACY-1",
            MarketplaceOrderId = order.Id,
            MarketplaceOrderItemId = item.Id,
            ExternalOrderId = order.MlOrderId,
            EconomicOccurredAt = order.PaidAt.Value,
            SourceEndpoint = "/legacy",
            CanonicalPayloadHash = new string('a', 64)
        };
        var head = new FinancialEconomicHead
        {
            TenantId = order.TenantId,
            ClientId = clientId,
            Provider = order.Provider,
            SellerId = order.SellerId,
            EconomicKey = entry.EconomicKey,
            ActiveEntryId = entry.Id
        };
        db.MarketplaceOrders.Add(order);
        db.MarketplaceFinancialEntries.Add(entry);
        db.FinancialEconomicHeads.Add(head);
        db.ProductVariants.Add(new ProductVariant
        {
            VariantSku = "PH-TEST", BaseSku = "PH-TEST", Name = "Teste",
            CatalogPriceCents = legacyUnitCost, CatalogPriceOrigin = CatalogPriceOrigins.VariantOverride,
            PricingMode = ProductPricingModes.Override
        });
        db.Publications.Add(new Publication
        {
            TenantId = order.TenantId, ClientId = clientId, ProductSku = "PH-TEST",
            CatalogPriceCentsSnapshot = publicationUnitCost, CostPriceCentsSnapshot = publicationUnitCost,
            FinalPriceCentsSnapshot = 4_990, CreatedByUserId = Guid.NewGuid(), UpdatedByUserId = Guid.NewGuid()
        });
        return (order, item, entry, head);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"legacy-cost-repair-{Guid.NewGuid():N}").Options);
}
