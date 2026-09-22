using Microsoft.EntityFrameworkCore;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Infrastructure.Persistence;

namespace Phub.Api.Tests;

public sealed class OwnedStockAndCostTests
{
    [Fact]
    public async Task CreatingLotTransfersGeneralAvailabilityWithoutChangingPhysicalStock()
    {
        await using var db = CreateDb();
        db.ProductVariants.Add(new ProductVariant
        {
            VariantSku = "PH-TEST", BaseSku = "PH-TEST", Name = "Teste", PhysicalStock = 100,
            AvailableStock = 98, SafetyBuffer = 2, CatalogPriceCents = 1500, CostPriceCents = 1000
        });
        await db.SaveChangesAsync();
        var service = new SellerOwnedStockLotService(db);

        var lot = await service.CreateFromSettledPurchaseAsync("tenant", Guid.NewGuid(), 123,
            "PH-TEST", 10, 1500, "purchase-1", DateTimeOffset.UtcNow, Guid.NewGuid());

        var variant = await db.ProductVariants.SingleAsync();
        Assert.Equal(100, variant.PhysicalStock);
        Assert.Equal(10, variant.ClientOwnedStock);
        Assert.Equal(88, variant.AvailableStock);
        Assert.Equal(2, variant.InventoryVersion);
        Assert.Equal(10, lot.AvailableQuantity);
    }

    [Fact]
    public async Task MixedReservationConsumesExactOriginsAndPreservesLotCost()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var variant = new ProductVariant
        {
            VariantSku = "PH-MIX", BaseSku = "PH-MIX", Name = "Misto", PhysicalStock = 20,
            ClientOwnedStock = 3, AvailableStock = 15, SafetyBuffer = 2, CatalogPriceCents = 1500, CostPriceCents = 1000
        };
        var lot = new SellerOwnedStockLot
        {
            TenantId = "tenant", ClientId = clientId, SellerId = 123, VariantSku = "PH-MIX",
            OriginalQuantity = 3, AvailableQuantity = 3, UnitCostCents = 900,
            SourceId = "purchase-1", AcquiredAt = DateTimeOffset.UtcNow.AddDays(-1), CreatedByUserId = Guid.NewGuid()
        };
        db.AddRange(variant, lot);
        await db.SaveChangesAsync();
        var reservation = new StockReservation
        {
            TenantId = "tenant", ClientId = clientId, SabrVariantSku = "PH-MIX",
            MarketplaceOrderId = Guid.NewGuid(), MarketplaceOrderItemId = Guid.NewGuid(), Quantity = 5
        };
        var service = new StockReservationAllocationService(db);

        Assert.True(await service.AllocateAsync(reservation, variant, 123));
        db.StockReservations.Add(reservation);
        await db.SaveChangesAsync();
        Assert.Equal(StockReservationSources.Mixed, reservation.Source);
        Assert.Equal(3, await db.StockReservationAllocations.Where(x => x.Source == StockReservationSources.PrePurchasedLot).SumAsync(x => x.Quantity));
        Assert.Equal(2, await db.StockReservationAllocations.Where(x => x.Source == StockReservationSources.GeneralStock).SumAsync(x => x.Quantity));

        await service.ConsumeAsync(reservation, variant, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        Assert.Equal(15, variant.PhysicalStock);
        Assert.Equal(0, variant.ClientOwnedStock);
        Assert.Equal(3, lot.ConsumedQuantity);
        Assert.All(await db.StockReservationAllocations.ToListAsync(), x => Assert.NotNull(x.ConsumedAt));
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"owned-stock-{Guid.NewGuid():N}").Options);
}
