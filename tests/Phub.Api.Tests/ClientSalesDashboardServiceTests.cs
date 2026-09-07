using Microsoft.EntityFrameworkCore;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Infrastructure.Persistence;

namespace Phub.Api.Tests;

public sealed class ClientSalesDashboardServiceTests
{
    [Fact]
    public async Task GetAsync_AggregatesSalesBySkuAndKeepsClientIsolation()
    {
        await using var db = CreateDb();
        const string tenantId = "tenant-dashboard";
        var clientId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var paid = CreateOrder(tenantId, clientId, "ORDER-PAID", "paid", now.AddDays(-1), 199.80m);
        paid.Items.Add(new MarketplaceOrderItem
        {
            TenantId = tenantId,
            ClientId = clientId,
            Provider = MarketplaceProvider.MercadoLivre,
            SellerId = paid.SellerId,
            MlItemId = "MLB-ITEM-01",
            ChannelSku = "CHANNEL-01",
            SabrVariantSku = "SKU-01",
            ProductName = "Produto campeão",
            Quantity = 2,
            UnitPrice = 99.90m,
            SaleFee = 20m,
            CurrencyId = "BRL",
            MappingState = "MAPPED",
            RawJson = "{}"
        });

        db.MarketplaceOrders.AddRange(
            paid,
            CreateOrder(tenantId, clientId, "ORDER-CANCELLED", "cancelled", now, null),
            CreateOrder(tenantId, Guid.NewGuid(), "ORDER-OTHER-CLIENT", "paid", now, 9999m));
        await db.SaveChangesAsync();

        var result = await new ClientSalesDashboardService(db).GetAsync(
            tenantId,
            clientId,
            now.AddDays(-2),
            now.AddMinutes(1),
            MarketplaceProvider.MercadoLivre);

        Assert.Equal(2, result.TotalOrders);
        Assert.Equal(1, result.PaidOrders);
        Assert.Equal(2, result.TotalUnits);
        Assert.Equal(199.80m, result.GrossRevenue);
        Assert.Equal(20m, result.MarketplaceFees);
        Assert.Equal(179.80m, result.NetRevenue);
        Assert.Equal(1, result.CancelledOrders);
        Assert.Equal("SKU-01", Assert.Single(result.TopSkus).Sku);
    }

    private static MarketplaceOrder CreateOrder(
        string tenantId,
        Guid clientId,
        string externalId,
        string status,
        DateTimeOffset occurredAt,
        decimal? totalAmount)
        => new()
        {
            TenantId = tenantId,
            ClientId = clientId,
            Provider = MarketplaceProvider.MercadoLivre,
            SellerId = 1001901,
            MlOrderId = externalId,
            Status = status,
            ChannelCreatedAt = occurredAt,
            ImportedAt = occurredAt,
            CurrencyId = "BRL",
            TotalAmount = totalAmount,
            PaidAmount = totalAmount
        };

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"sales-dashboard-{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options);
    }
}
