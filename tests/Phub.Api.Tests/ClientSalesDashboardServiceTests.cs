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
        Assert.Equal("SKU-01", Assert.Single(result.Products).Sku);
        Assert.Equal(1, result.TotalProducts);
    }

    [Fact]
    public async Task GetAsync_ListsEveryProductAndDoesNotCollapseItemsWithoutSku()
    {
        await using var db = CreateDb();
        const string tenantId = "tenant-all-products";
        var clientId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        for (var index = 1; index <= 12; index++)
        {
            var order = CreateOrder(tenantId, clientId, $"ORDER-{index}", "paid", now.AddMinutes(-index), 10m);
            order.Items.Add(new MarketplaceOrderItem
            {
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = order.SellerId,
                MlItemId = $"MLB-{index}",
                ProductName = $"Produto {index}",
                Quantity = index,
                UnitPrice = 10m,
                MappingState = "UNMAPPED",
                RawJson = "{}"
            });
            db.MarketplaceOrders.Add(order);
        }

        await db.SaveChangesAsync();

        var result = await new ClientSalesDashboardService(db).GetAsync(
            tenantId, clientId, now.AddDays(-1), now.AddMinutes(1), MarketplaceProvider.MercadoLivre);

        Assert.Equal(12, result.TotalProducts);
        Assert.Equal(12, result.Products.Count);
        Assert.Equal(10, result.TopSkus.Count);
        Assert.Equal(12, result.Products.Select(product => product.ChannelItemId).Distinct().Count());
    }

    [Fact]
    public async Task GetAsync_ListsOnlyPendingShipmentsDueTodayBySku()
    {
        await using var db = CreateDb();
        const string tenantId = "tenant-shipping-today";
        var clientId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var timeZone = ResolveSaoPauloTimeZone();
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var todayDeadline = new DateTimeOffset(
            localNow.Year, localNow.Month, localNow.Day, 18, 0, 0, timeZone.GetUtcOffset(localNow.Date));

        var pending = CreateOrder(tenantId, clientId, "ORDER-TODAY", "paid", now, 30m);
        pending.ShipmentId = "SHIP-TODAY";
        pending.ShipByDeadlineAt = todayDeadline;
        pending.SabrPaymentConfirmedAt = now;
        pending.Items.Add(new MarketplaceOrderItem
        {
            TenantId = tenantId,
            ClientId = clientId,
            Provider = MarketplaceProvider.MercadoLivre,
            SellerId = pending.SellerId,
            MlItemId = "MLB-TODAY",
            SabrVariantSku = "SKU-TODAY",
            ProductName = "Produto para enviar",
            Quantity = 3,
            UnitPrice = 10m,
            MappingState = "MAPPED",
            RawJson = "{}"
        });

        var shipped = CreateOrder(tenantId, clientId, "ORDER-SHIPPED", "paid", now, 10m);
        shipped.ShipmentId = "SHIP-SHIPPED";
        shipped.ShipByDeadlineAt = todayDeadline;
        shipped.Items.Add(new MarketplaceOrderItem
        {
            TenantId = tenantId,
            ClientId = clientId,
            Provider = MarketplaceProvider.MercadoLivre,
            SellerId = shipped.SellerId,
            MlItemId = "MLB-SHIPPED",
            Quantity = 1,
            RawJson = "{}"
        });

        db.MarketplaceOrders.AddRange(pending, shipped);
        db.MarketplaceShipments.Add(new MarketplaceShipment
        {
            TenantId = tenantId,
            ClientId = clientId,
            Provider = MarketplaceProvider.MercadoLivre,
            SellerId = shipped.SellerId,
            ShipmentId = shipped.ShipmentId,
            MlOrderId = shipped.MlOrderId,
            ShipByDeadlineAt = todayDeadline,
            ShippedAt = now,
            Status = "shipped"
        });
        await db.SaveChangesAsync();

        var result = await new ClientSalesDashboardService(db).GetAsync(
            tenantId, clientId, now.AddDays(-1), now.AddMinutes(1), MarketplaceProvider.MercadoLivre);

        Assert.Equal(DateOnly.FromDateTime(localNow.Date), result.ShippingToday.DueDate);
        Assert.Equal(1, result.ShippingToday.TotalOrders);
        Assert.Equal(1, result.ShippingToday.PaidOrders);
        Assert.Equal(3, result.ShippingToday.TotalUnits);
        Assert.Equal("SKU-TODAY", Assert.Single(result.ShippingToday.Products).Sku);
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

    private static TimeZoneInfo ResolveSaoPauloTimeZone()
    {
        foreach (var id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return TimeZoneInfo.Utc;
    }
}
