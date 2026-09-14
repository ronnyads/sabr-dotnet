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
