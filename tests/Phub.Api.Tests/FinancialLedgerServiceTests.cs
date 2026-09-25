using Microsoft.EntityFrameworkCore;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Infrastructure.Persistence;

namespace Phub.Api.Tests;

public sealed class FinancialLedgerServiceTests
{
    [Fact]
    public async Task CancelledOrder_VoidsSaleAndProductCost_ButKeepsRealReturnExpense()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var order = new MarketplaceOrder
        {
            TenantId = "tenant-cancel", ClientId = clientId, SellerId = 20,
            Provider = MarketplaceProvider.MercadoLivre, MlOrderId = "ORDER-CANCEL",
            Status = "paid", PaidAt = DateTimeOffset.UtcNow.AddDays(-1), RawJson = "{}"
        };
        var item = new MarketplaceOrderItem
        {
            TenantId = order.TenantId, ClientId = clientId, SellerId = order.SellerId,
            MarketplaceOrderId = order.Id, Provider = order.Provider, MlItemId = "MLB-CANCEL",
            SabrVariantSku = "PH-CANCEL", MappingState = MarketplaceMappingStates.Mapped,
            Quantity = 1, UnitPrice = 100m, SaleFee = 10m, RawJson = "{}"
        };
        db.MarketplaceOrders.Add(order);
        db.MarketplaceOrderItems.Add(item);
        db.ProductVariants.Add(new ProductVariant
        {
            BaseSku = "PH-CANCEL", VariantSku = "PH-CANCEL", Name = "Produto cancelado",
            CatalogPriceCents = 3_000, PhysicalStock = 10, AvailableStock = 10
        });
        await db.SaveChangesAsync();
        var ledger = new FinancialLedgerService(db);
        var projection = new OperationalFinancialProjectionService(db, ledger);

        await projection.ProjectOrderAsync(order.Id);
        await ledger.AppendAsync(new AppendFinancialEntryRequest
        {
            TenantId = order.TenantId, ClientId = clientId, Provider = order.Provider, SellerId = order.SellerId,
            EntryType = FinancialEntryTypes.ReturnShippingCost, Layer = FinancialLayers.Operational,
            Status = FinancialEntryStatuses.Estimated, AmountCents = -1_500, CurrencyId = "BRL",
            EconomicKey = $"ML:{order.SellerId}:ORDER:{order.MlOrderId}:RETURN_SHIPPING",
            IdempotencyKey = "cancel-return-shipping", MarketplaceOrderId = order.Id,
            ExternalOrderId = order.MlOrderId, EconomicOccurredAt = DateTimeOffset.UtcNow,
            SourceEndpoint = "/claims/return", SourceRecordId = "return-1",
            CanonicalPayloadHash = new string('a', 64), MetadataJson = "{}"
        });

        order.Status = "cancelled";
        order.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await projection.ProjectOrderAsync(order.Id);
        // A retry must not create another void version.
        await projection.ProjectOrderAsync(order.Id);

        var heads = await (from head in db.FinancialEconomicHeads
                           join entry in db.MarketplaceFinancialEntries on head.ActiveEntryId equals entry.Id
                           where entry.MarketplaceOrderId == order.Id
                           select entry).ToListAsync();
        Assert.All(heads.Where(x => x.EntryType is FinancialEntryTypes.GrossSale
            or FinancialEntryTypes.SaleFee or FinancialEntryTypes.ProductCost),
            entry => Assert.Equal(FinancialEntryStatuses.Voided, entry.Status));
        Assert.Equal(FinancialEntryStatuses.Estimated,
            Assert.Single(heads.Where(x => x.EntryType == FinancialEntryTypes.ReturnShippingCost)).Status);
        Assert.Equal(3, heads.Count(x => x.Status == FinancialEntryStatuses.Voided));

        var result = await new FinancialProfitabilityService(db).GetAsync(
            order.TenantId, clientId, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(1),
            MarketplaceProvider.MercadoLivre, order.SellerId);
        Assert.Equal(0, result.GrossRevenueCents);
        Assert.Equal(0, result.ProductCostCents);
        Assert.Equal(-1_500, result.OperationalProfitCents);
    }

    [Fact]
    public async Task RefundedOrder_KeepsOriginalSaleAndCost_AndAddsRefundAsReverseFact()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var order = new MarketplaceOrder
        {
            TenantId = "tenant-refund", ClientId = clientId, SellerId = 21,
            Provider = MarketplaceProvider.MercadoLivre, MlOrderId = "ORDER-REFUND",
            Status = "refunded", PaidAt = DateTimeOffset.UtcNow.AddDays(-1),
            RawJson = "{\"payments\":[{\"id\":123,\"refunds\":[{\"id\":456,\"amount\":100.0}]}]}"
        };
        db.MarketplaceOrders.Add(order);
        db.MarketplaceOrderItems.Add(new MarketplaceOrderItem
        {
            TenantId = order.TenantId, ClientId = clientId, SellerId = order.SellerId,
            MarketplaceOrderId = order.Id, Provider = order.Provider, MlItemId = "MLB-REFUND",
            SabrVariantSku = "PH-REFUND", MappingState = MarketplaceMappingStates.Mapped,
            Quantity = 1, UnitPrice = 100m, SaleFee = 0m, RawJson = "{}"
        });
        db.ProductVariants.Add(new ProductVariant
        {
            BaseSku = "PH-REFUND", VariantSku = "PH-REFUND", Name = "Produto reembolsado",
            CatalogPriceCents = 3_000, PhysicalStock = 10, AvailableStock = 10
        });
        await db.SaveChangesAsync();

        await new OperationalFinancialProjectionService(db, new FinancialLedgerService(db)).ProjectOrderAsync(order.Id);
        var result = await new FinancialProfitabilityService(db).GetAsync(
            order.TenantId, clientId, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(1),
            MarketplaceProvider.MercadoLivre, order.SellerId);

        Assert.Equal(10_000, result.GrossRevenueCents);
        Assert.Equal(10_000, result.RefundsCents);
        Assert.Equal(-3_000, result.ProductCostCents);
        Assert.Equal(-3_000, result.OperationalProfitCents);
    }

    [Fact]
    public async Task ProductCost_UsesInternalPaidSnapshotAndConfirmsWithoutRewritingEstimate()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var order = new MarketplaceOrder
        {
            TenantId = "tenant", ClientId = clientId, SellerId = 10,
            Provider = MarketplaceProvider.MercadoLivre, MlOrderId = "ORDER-COST-SNAPSHOT",
            Status = "paid", PaidAt = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero), RawJson = "{}"
        };
        var item = new MarketplaceOrderItem
        {
            TenantId = order.TenantId, ClientId = clientId, SellerId = 10,
            MarketplaceOrderId = order.Id, Provider = order.Provider, MlItemId = "MLB-COST",
            SabrVariantSku = "PH-COST", MappingState = MarketplaceMappingStates.Mapped,
            Quantity = 2, UnitPrice = 30m, RawJson = "{}"
        };
        db.MarketplaceOrders.Add(order);
        db.MarketplaceOrderItems.Add(item);
        db.ProductVariants.Add(new ProductVariant
        {
            BaseSku = "PH-COST", VariantSku = "PH-COST", Name = "Produto interno",
            CatalogPriceCents = 800, PhysicalStock = 10, AvailableStock = 8
        });
        db.ProductPriceVersions.Add(new ProductPriceVersion
        {
            ProductSku = "PH-COST", VariantSku = "PH-COST", PricingMode = ProductPricingModes.Inherited,
            CatalogPriceCents = 800, CostPriceCents = 0,
            CatalogCostStatus = CatalogCostStatuses.Resolved,
            CatalogPriceOrigin = CatalogPriceOrigins.MasterProduct,
            ValidFrom = order.PaidAt.Value.AddDays(-1), Version = 1,
            ChangedByUserId = Guid.NewGuid(), Reason = "Preço interno histórico"
        });
        await db.SaveChangesAsync();
        var projection = new OperationalFinancialProjectionService(db, new FinancialLedgerService(db));

        await projection.ProjectOrderAsync(order.Id);
        item.CatalogUnitPriceCentsAtPayment = 1_000;
        (await db.ProductVariants.SingleAsync()).CatalogPriceCents = 1_200;
        await db.SaveChangesAsync();
        await projection.ProjectOrderAsync(order.Id);
        await projection.ConfirmProductCostsAsync(order.Id, DateTimeOffset.UtcNow);

        var costs = await db.MarketplaceFinancialEntries.Where(x => x.EntryType == FinancialEntryTypes.ProductCost)
            .OrderBy(x => x.ObservedAt).ToListAsync();
        Assert.Equal(3, costs.Count);
        Assert.Equal(-1_600, costs[0].AmountCents);
        Assert.Equal(-2_000, costs[1].AmountCents);
        Assert.Equal(-2_000, costs[2].AmountCents);
        Assert.Equal(FinancialEntryStatuses.Confirmed, costs[2].Status);
        Assert.Equal(costs[2].Id, (await db.FinancialEconomicHeads.SingleAsync(x => x.EconomicKey.Contains("PRODUCT_COST"))).ActiveEntryId);
    }

    [Fact]
    public async Task AppendAsync_PreservesEstimateAndMovesSingleEconomicHeadToConfirmation()
    {
        await using var db = CreateDb();
        var service = new FinancialLedgerService(db);
        var client = Guid.NewGuid();
        var identity = CreateRequest(FinancialEntryTypes.SaleFee, -1250, "estimate-v1", FinancialEntryStatuses.Estimated, client: client);
        var estimated = await service.AppendAsync(identity);
        var duplicate = await service.AppendAsync(identity);
        Assert.Equal(estimated.Id, duplicate.Id);

        var confirmedRequest = CreateRequest(FinancialEntryTypes.SaleFee, -1275, "billing-v1", FinancialEntryStatuses.Confirmed, client: client);
        confirmedRequest.FinancialConfirmedAt = DateTimeOffset.UtcNow;
        confirmedRequest.Layer = FinancialLayers.Reconciled;
        var confirmed = await service.AppendAsync(confirmedRequest);

        Assert.Equal(estimated.Id, confirmed.SupersedesEntryId);
        Assert.Equal(2, await db.MarketplaceFinancialEntries.CountAsync());
        var head = await db.FinancialEconomicHeads.SingleAsync();
        Assert.Equal(confirmed.Id, head.ActiveEntryId);
        Assert.Equal(2, head.Version);
    }

    [Fact]
    public async Task AppendAsync_RejectsInvalidSignAndConfirmedEntryWithoutConfirmationDate()
    {
        await using var db = CreateDb();
        var service = new FinancialLedgerService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => service.AppendAsync(
            CreateRequest(FinancialEntryTypes.GrossSale, -100, "bad-sign", FinancialEntryStatuses.Estimated)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AppendAsync(
            CreateRequest(FinancialEntryTypes.Refund, -100, "missing-confirmation", FinancialEntryStatuses.Confirmed)));
    }

    [Fact]
    public async Task AppendAsync_NormalizesProviderLocalOffsetsToUtc()
    {
        await using var db = CreateDb();
        var service = new FinancialLedgerService(db);
        var request = CreateRequest(FinancialEntryTypes.GrossSale, 100, "offset-v1", FinancialEntryStatuses.Confirmed);
        request.FinancialConfirmedAt = new DateTimeOffset(2026, 9, 18, 22, 30, 0, TimeSpan.FromHours(-4));
        request.EconomicOccurredAt = request.FinancialConfirmedAt.Value.AddMinutes(-10);
        request.ProviderUpdatedAt = request.FinancialConfirmedAt;

        var entry = await service.AppendAsync(request);

        Assert.Equal(TimeSpan.Zero, entry.EconomicOccurredAt.Offset);
        Assert.Equal(TimeSpan.Zero, entry.FinancialConfirmedAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, entry.ProviderUpdatedAt!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 9, 19, 2, 20, 0, TimeSpan.Zero), entry.EconomicOccurredAt);
    }

    [Fact]
    public async Task Profitability_KeepsUnallocatedRefundOutsideSkuAllocationAndReportsDelta()
    {
        await using var db = CreateDb();
        var tenant = "tenant-finance";
        var client = Guid.NewGuid();
        var order = new MarketplaceOrder
        {
            TenantId = tenant, ClientId = client, Provider = MarketplaceProvider.MercadoLivre, SellerId = 991,
            MlOrderId = "ORDER-1", Status = "paid", PaidAt = DateTimeOffset.UtcNow.AddDays(-1), ImportedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        db.MarketplaceOrders.Add(order);
        db.MarketplaceOrderFinancialStates.Add(new MarketplaceOrderFinancialState
        {
            MarketplaceOrderId = order.Id, TenantId = tenant, ClientId = client, Provider = MarketplaceProvider.MercadoLivre,
            SellerId = 991, Maturity = FinancialMaturity.Incomplete, UnallocatedCents = -500,
            ItemAllocationResolved = false, SkuResolved = true, CostResolved = true, FreightResolved = true
        });
        await db.SaveChangesAsync();
        var ledger = new FinancialLedgerService(db);
        var estimated = CreateRequest(FinancialEntryTypes.Refund, -450, "refund-est", FinancialEntryStatuses.Estimated, tenant, client, order.Id, 991);
        estimated.EconomicKey = "ML:991:PAYMENT:1:REFUND:1";
        await ledger.AppendAsync(estimated);
        var confirmed = CreateRequest(FinancialEntryTypes.Refund, -500, "refund-confirmed", FinancialEntryStatuses.Confirmed, tenant, client, order.Id, 991);
        confirmed.EconomicKey = estimated.EconomicKey;
        confirmed.Layer = FinancialLayers.Reconciled;
        confirmed.FinancialConfirmedAt = DateTimeOffset.UtcNow;
        await ledger.AppendAsync(confirmed);

        var result = await new FinancialProfitabilityService(db).GetAsync(tenant, client,
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow, MarketplaceProvider.MercadoLivre, 991);
        Assert.Equal(-50, result.Divergence.AbsoluteCents);
        Assert.Equal(-50, result.Divergence.ComponentsCents["refunds"]);
        Assert.Equal(-500, result.UnallocatedCents);
        Assert.Equal(0, result.Coverage.ItemAllocationPercent);
    }

    [Fact]
    public async Task Profitability_DivergenceOnlyCountsKeysWithBothEstimateAndConfirmation()
    {
        // Regressão do achado 2.6 da auditoria: uma confirmação sem estimativa prévia
        // (ou vice-versa) não pode virar divergência total, conforme a regra do plano
        // ("falta de confirmação não equivale a diferença negativa"). componentDeltas
        // já respeitava isso; os totais do topo (AbsoluteCents) não respeitavam.
        await using var db = CreateDb();
        var tenant = "tenant-finance-pairing";
        var client = Guid.NewGuid();
        var order = new MarketplaceOrder
        {
            TenantId = tenant, ClientId = client, Provider = MarketplaceProvider.MercadoLivre, SellerId = 992,
            MlOrderId = "ORDER-PAIRING", Status = "paid", PaidAt = DateTimeOffset.UtcNow.AddDays(-1), ImportedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        db.MarketplaceOrders.Add(order);
        await db.SaveChangesAsync();
        var ledger = new FinancialLedgerService(db);

        // Paired key: estimated -450, confirmed -500 => delta -50. This is the only
        // amount the top-level divergence should reflect.
        var pairedEstimated = CreateRequest(FinancialEntryTypes.Refund, -450, "pair-est", FinancialEntryStatuses.Estimated, tenant, client, order.Id, 992);
        pairedEstimated.EconomicKey = "ML:992:PAYMENT:1:REFUND:1";
        await ledger.AppendAsync(pairedEstimated);
        var pairedConfirmed = CreateRequest(FinancialEntryTypes.Refund, -500, "pair-conf", FinancialEntryStatuses.Confirmed, tenant, client, order.Id, 992);
        pairedConfirmed.EconomicKey = pairedEstimated.EconomicKey;
        pairedConfirmed.Layer = FinancialLayers.Reconciled;
        pairedConfirmed.FinancialConfirmedAt = DateTimeOffset.UtcNow;
        await ledger.AppendAsync(pairedConfirmed);

        // Unpaired: a confirmed fee with no prior estimate at all (different economic
        // key). Before the fix, its full -1000 would be folded into confirmedTotal and
        // therefore into AbsoluteCents, even though there is nothing to compare it to.
        var unpairedConfirmed = CreateRequest(FinancialEntryTypes.SaleFee, -1000, "unpaired-conf", FinancialEntryStatuses.Confirmed, tenant, client, order.Id, 992);
        unpairedConfirmed.EconomicKey = "ML:992:PAYMENT:1:FEE:1";
        unpairedConfirmed.Layer = FinancialLayers.Reconciled;
        unpairedConfirmed.FinancialConfirmedAt = DateTimeOffset.UtcNow;
        await ledger.AppendAsync(unpairedConfirmed);

        var result = await new FinancialProfitabilityService(db).GetAsync(tenant, client,
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow, MarketplaceProvider.MercadoLivre, 992);

        Assert.Equal(-50, result.Divergence.AbsoluteCents);
        Assert.False(result.Divergence.ComponentsCents.ContainsKey("commission"));
    }

    [Fact]
    public async Task Profitability_KeepsTotalsInOneCurrency_WhenEntriesAreMixed()
    {
        // Regressão do achado 2.6 da auditoria: somar AmountCents entre moedas
        // diferentes produz um número sem sentido. Os totais devem ficar restritos à
        // mesma moeda dominante que o próprio CurrencyId da resposta.
        await using var db = CreateDb();
        var tenant = "tenant-finance-currency";
        var client = Guid.NewGuid();
        var order = new MarketplaceOrder
        {
            TenantId = tenant, ClientId = client, Provider = MarketplaceProvider.MercadoLivre, SellerId = 993,
            MlOrderId = "ORDER-CURRENCY", Status = "paid", PaidAt = DateTimeOffset.UtcNow.AddDays(-1), ImportedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        db.MarketplaceOrders.Add(order);
        await db.SaveChangesAsync();
        var ledger = new FinancialLedgerService(db);

        var brlSale = CreateRequest(FinancialEntryTypes.GrossSale, 10_000, "brl-sale", FinancialEntryStatuses.Confirmed, tenant, client, order.Id, 993);
        brlSale.EconomicKey = "ML:993:PAYMENT:1:GROSS:1";
        brlSale.Layer = FinancialLayers.Reconciled;
        brlSale.FinancialConfirmedAt = DateTimeOffset.UtcNow;
        brlSale.CurrencyId = "BRL";
        await ledger.AppendAsync(brlSale);

        var usdSale = CreateRequest(FinancialEntryTypes.GrossSale, 5_000, "usd-sale", FinancialEntryStatuses.Confirmed, tenant, client, order.Id, 993);
        usdSale.EconomicKey = "ML:993:PAYMENT:2:GROSS:1";
        usdSale.Layer = FinancialLayers.Reconciled;
        usdSale.FinancialConfirmedAt = DateTimeOffset.UtcNow;
        usdSale.CurrencyId = "USD";
        await ledger.AppendAsync(usdSale);

        var result = await new FinancialProfitabilityService(db).GetAsync(tenant, client,
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow, MarketplaceProvider.MercadoLivre, 993);

        var expected = result.CurrencyId == "BRL" ? 10_000 : 5_000;
        Assert.Equal(expected, result.GrossRevenueCents);
        Assert.NotEqual(15_000, result.GrossRevenueCents);
    }

    [Fact]
    public async Task Projection_LeavesMarketplaceFeePending_WhenProviderDidNotSupplyIt()
    {
        await using var db = CreateDb();
        var order = new MarketplaceOrder
        {
            TenantId = "tenant-fee-pending", ClientId = Guid.NewGuid(), Provider = MarketplaceProvider.MercadoLivre,
            SellerId = 994, MlOrderId = "ORDER-FEE-PENDING", Status = "paid", ImportedAt = DateTimeOffset.UtcNow
        };
        order.Items.Add(new MarketplaceOrderItem { Quantity = 1, UnitPrice = 100m, SaleFee = null });
        db.MarketplaceOrders.Add(order);
        await db.SaveChangesAsync();

        var projection = new OperationalFinancialProjectionService(db, new FinancialLedgerService(db));
        await projection.RebuildOrderStateAsync(order);
        var state = await db.MarketplaceOrderFinancialStates.SingleAsync();
        Assert.Equal(FinancialMaturity.Incomplete, state.Maturity);
        Assert.Contains("MARKETPLACE_FEE_PENDING", state.IncompleteReasonsJson);

        order.Items.Single().SaleFee = 0m;
        await db.SaveChangesAsync();
        await projection.RebuildOrderStateAsync(order);
        Assert.DoesNotContain("MARKETPLACE_FEE_PENDING", state.IncompleteReasonsJson);
    }

    [Fact]
    public async Task Profitability_UsesMarketplaceNetLessInternalCatalogCost()
    {
        await using var db = CreateDb();
        var tenant = "tenant-profit-equation";
        var client = Guid.NewGuid();
        var order = new MarketplaceOrder
        {
            TenantId = tenant, ClientId = client, Provider = MarketplaceProvider.MercadoLivre,
            SellerId = 995, MlOrderId = "ORDER-PROFIT", Status = "paid",
            PaidAt = DateTimeOffset.UtcNow.AddDays(-1), ImportedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        db.MarketplaceOrders.Add(order);
        await db.SaveChangesAsync();
        var ledger = new FinancialLedgerService(db);
        var facts = new (string Type, long Cents)[]
        {
            (FinancialEntryTypes.GrossSale, 10_000),
            (FinancialEntryTypes.SaleFee, -1_000),
            (FinancialEntryTypes.SellerShippingCost, -500),
            (FinancialEntryTypes.Refund, -300),
            (FinancialEntryTypes.PlatformAdjustment, 200),
            (FinancialEntryTypes.ProductCost, -3_000)
        };
        foreach (var (type, cents) in facts)
            await ledger.AppendAsync(CreateRequest(type, cents, type, FinancialEntryStatuses.Estimated, tenant, client, order.Id, 995));

        var result = await new FinancialProfitabilityService(db).GetAsync(tenant, client,
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow, MarketplaceProvider.MercadoLivre, 995);

        Assert.Equal(10_000, result.GrossRevenueCents);
        Assert.Equal(1_000, result.MarketplaceFeesCents);
        Assert.Equal(500, result.SellerShippingCents);
        Assert.Equal(300, result.RefundsCents);
        Assert.Equal(200, result.AdjustmentsCents);
        Assert.Equal(8_400, result.MarketplaceNetAmountCents);
        Assert.Equal(-3_000, result.ProductCostCents);
        Assert.Equal(5_400, result.OperationalProfitCents);
        Assert.Equal(54m, result.OperationalMarginPct);
    }

    [Fact]
    public async Task ConfirmedMarketplaceValue_DoesNotIncludeInternalConfirmedProductCost()
    {
        await using var db = CreateDb();
        var tenant = "tenant-confirmed-separation";
        var client = Guid.NewGuid();
        var order = new MarketplaceOrder
        {
            TenantId = tenant, ClientId = client, Provider = MarketplaceProvider.MercadoLivre,
            SellerId = 996, MlOrderId = "ORDER-CONFIRMED-SEPARATION", Status = "paid",
            PaidAt = DateTimeOffset.UtcNow.AddDays(-1), ImportedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        db.MarketplaceOrders.Add(order);
        await db.SaveChangesAsync();
        var ledger = new FinancialLedgerService(db);

        var external = CreateRequest(FinancialEntryTypes.GrossSale, 8_500, "external-confirmed",
            FinancialEntryStatuses.Confirmed, tenant, client, order.Id, 996);
        external.Layer = FinancialLayers.Reconciled;
        external.FinancialConfirmedAt = DateTimeOffset.UtcNow;
        await ledger.AppendAsync(external);

        var internalCost = CreateRequest(FinancialEntryTypes.ProductCost, -3_000, "internal-confirmed",
            FinancialEntryStatuses.Confirmed, tenant, client, order.Id, 996);
        internalCost.Layer = FinancialLayers.InternalConfirmed;
        internalCost.FinancialConfirmedAt = DateTimeOffset.UtcNow;
        await ledger.AppendAsync(internalCost);

        var result = await new FinancialProfitabilityService(db).GetAsync(tenant, client,
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow, MarketplaceProvider.MercadoLivre, 996);

        Assert.Equal(8_500, result.ReconciledConfirmedValueCents);
        Assert.Equal(-3_000, result.ProductCostCents);
        Assert.Equal(5_500, result.OperationalProfitCents);
    }

    [Fact]
    public async Task Projection_UsesExternalCostSnapshot_WithoutTreatingExternalItemAsSkuPending()
    {
        await using var db = CreateDb();
        var tenant = "tenant-external-cost";
        var client = Guid.NewGuid();
        var order = new MarketplaceOrder
        {
            TenantId = tenant, ClientId = client, Provider = MarketplaceProvider.MercadoLivre,
            SellerId = 997, MlOrderId = "ORDER-EXTERNAL-COST", Status = "paid",
            PaidAt = DateTimeOffset.UtcNow.AddDays(-1), ImportedAt = DateTimeOffset.UtcNow.AddDays(-1), CurrencyId = "BRL"
        };
        order.Items.Add(new MarketplaceOrderItem
        {
            TenantId = tenant, ClientId = client, Provider = order.Provider, SellerId = order.SellerId,
            MlItemId = "EXT-1", Quantity = 2, UnitPrice = 50m, SaleFee = 10m,
            MappingState = "EXTERNAL_SUPPLIER", ExternalSupplierName = "Fornecedor externo",
            ExternalUnitCostCentsSnapshot = 1_500, ExternalCostVersionId = Guid.NewGuid(), RawJson = "{}"
        });
        db.MarketplaceOrders.Add(order);
        await db.SaveChangesAsync();

        var projection = new OperationalFinancialProjectionService(db, new FinancialLedgerService(db));
        await projection.ProjectOrderAsync(order.Id);

        var state = await db.MarketplaceOrderFinancialStates.SingleAsync();
        Assert.True(state.SkuResolved);
        Assert.True(state.CostResolved);
        Assert.DoesNotContain("SKU_PENDING", state.IncompleteReasonsJson);
        Assert.DoesNotContain("EXTERNAL_COST_PENDING", state.IncompleteReasonsJson);
        var cost = await db.MarketplaceFinancialEntries.SingleAsync(x => x.EntryType == FinancialEntryTypes.ProductCost);
        Assert.Equal(-3_000, cost.AmountCents);
        Assert.Contains("EXTERNAL_SUPPLIER", cost.MetadataJson);
    }

    [Fact]
    public async Task Projection_ExternalItemWithoutCost_RemainsExplicitlyIncomplete()
    {
        await using var db = CreateDb();
        var order = new MarketplaceOrder
        {
            TenantId = "tenant-external-pending", ClientId = Guid.NewGuid(), Provider = MarketplaceProvider.MercadoLivre,
            SellerId = 998, MlOrderId = "ORDER-EXTERNAL-PENDING", Status = "paid",
            PaidAt = DateTimeOffset.UtcNow.AddDays(-1), ImportedAt = DateTimeOffset.UtcNow.AddDays(-1), CurrencyId = "BRL"
        };
        order.Items.Add(new MarketplaceOrderItem
        {
            TenantId = order.TenantId, ClientId = order.ClientId, Provider = order.Provider, SellerId = order.SellerId,
            MlItemId = "EXT-2", Quantity = 1, UnitPrice = 60m, SaleFee = 6m,
            MappingState = "EXTERNAL_COST_PENDING", ExternalSupplierName = "Fornecedor externo", RawJson = "{}"
        });
        db.MarketplaceOrders.Add(order);
        await db.SaveChangesAsync();

        var projection = new OperationalFinancialProjectionService(db, new FinancialLedgerService(db));
        await projection.ProjectOrderAsync(order.Id);

        var state = await db.MarketplaceOrderFinancialStates.SingleAsync();
        Assert.Equal(FinancialMaturity.Incomplete, state.Maturity);
        Assert.True(state.SkuResolved);
        Assert.False(state.CostResolved);
        Assert.Contains("EXTERNAL_COST_PENDING", state.IncompleteReasonsJson);
        Assert.DoesNotContain("SKU_PENDING", state.IncompleteReasonsJson);
        Assert.DoesNotContain("CATALOG_COST_PENDING", state.IncompleteReasonsJson);

        var profitability = await new FinancialProfitabilityService(db).GetAsync(
            order.TenantId, order.ClientId, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow,
            order.Provider, order.SellerId);
        Assert.Equal(0, profitability.GrossRevenueCents);
        Assert.Equal(1, profitability.ExternalSupplier.ItemsPendingCost);
        Assert.Null(profitability.ExternalSupplier.OperationalProfitCents);
    }

    [Fact]
    public async Task Profitability_ExternalBucket_DoesNotInventAllocationForOrderLevelValues()
    {
        await using var db = CreateDb();
        var tenant = "tenant-external-bucket";
        var client = Guid.NewGuid();
        var order = new MarketplaceOrder
        {
            TenantId = tenant, ClientId = client, Provider = MarketplaceProvider.MercadoLivre,
            SellerId = 999, MlOrderId = "ORDER-EXTERNAL-BUCKET", Status = "paid",
            PaidAt = DateTimeOffset.UtcNow.AddDays(-1), ImportedAt = DateTimeOffset.UtcNow.AddDays(-1), CurrencyId = "BRL"
        };
        var item = new MarketplaceOrderItem
        {
            TenantId = tenant, ClientId = client, Provider = order.Provider, SellerId = order.SellerId,
            MlItemId = "EXT-3", Quantity = 1, UnitPrice = 100m, SaleFee = 10m,
            MappingState = MarketplaceMappingStates.ExternalSupplier, ExternalSupplierName = "Fornecedor externo",
            ExternalUnitCostCentsSnapshot = 3_000, ExternalCostVersionId = Guid.NewGuid(), RawJson = "{}"
        };
        order.Items.Add(item);
        db.MarketplaceOrders.Add(order);
        await db.SaveChangesAsync();
        var ledger = new FinancialLedgerService(db);
        foreach (var (type, cents, allocated) in new[]
                 {
                     (FinancialEntryTypes.GrossSale, 10_000L, true),
                     (FinancialEntryTypes.SaleFee, -1_000L, true),
                     (FinancialEntryTypes.ProductCost, -3_000L, true),
                     (FinancialEntryTypes.SellerShippingCost, -500L, false),
                     (FinancialEntryTypes.Refund, -200L, false)
                 })
        {
            var request = CreateRequest(type, cents, $"external-{type}", FinancialEntryStatuses.Estimated,
                tenant, client, order.Id, order.SellerId);
            request.EconomicKey = $"ML:{order.SellerId}:ORDER:{order.MlOrderId}:{type}";
            request.MarketplaceOrderItemId = allocated ? item.Id : null;
            await ledger.AppendAsync(request);
        }

        var result = await new FinancialProfitabilityService(db).GetAsync(tenant, client,
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow, order.Provider, order.SellerId);

        Assert.True(result.ExternalSupplier.IsComplete);
        Assert.Equal(10_000, result.ExternalSupplier.GrossRevenueCents);
        Assert.Equal(9_000, result.ExternalSupplier.AllocatedMarketplaceNetCents);
        Assert.Equal(-3_000, result.ExternalSupplier.ProductCostCents);
        Assert.Equal(6_000, result.ExternalSupplier.OperationalProfitCents);
        Assert.Equal(8_300, result.MarketplaceNetAmountCents); // includes unallocated shipping/refund at order grain
    }

    private static AppendFinancialEntryRequest CreateRequest(string type, long cents, string idempotency, string status,
        string tenant = "tenant", Guid? client = null, Guid? order = null, long seller = 10) => new()
    {
        TenantId = tenant, ClientId = client ?? Guid.NewGuid(), Provider = MarketplaceProvider.MercadoLivre, SellerId = seller,
        EntryType = type, Layer = FinancialLayers.Operational, Status = status, AmountCents = cents, CurrencyId = "BRL",
        EconomicKey = $"ML:{seller}:ORDER:1:ITEM:1:{type}", IdempotencyKey = idempotency,
        MarketplaceOrderId = order, EconomicOccurredAt = DateTimeOffset.UtcNow.AddDays(-1),
        SourceEndpoint = "/test", CanonicalPayloadHash = new string('a', 64)
    };

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"financial-ledger-{Guid.NewGuid():N}").Options);
}
