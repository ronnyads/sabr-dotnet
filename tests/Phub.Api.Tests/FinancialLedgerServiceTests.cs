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
