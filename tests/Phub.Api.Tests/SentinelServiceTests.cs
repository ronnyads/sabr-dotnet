using Microsoft.EntityFrameworkCore;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Infrastructure.Persistence;

namespace Phub.Api.Tests;

public sealed class SentinelServiceTests
{
    [Fact]
    public async Task Summary_DerivesPackedAwaitingConfirmationAndCriticalRisk()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var clientId = Guid.NewGuid();
        SeedShipment(db, clientId, now, lastSyncAt: now, deadlineAt: now.AddMinutes(20), packedAt: now.AddMinutes(-2));
        await db.SaveChangesAsync();

        var result = await new SentinelService(db).SummaryAsync(Filter(), CancellationToken.None);

        Assert.Equal(1, result.TotalOpen);
        Assert.Equal(1, result.PackedAwaitingConfirmation);
        Assert.Equal(1, result.Critical);
        Assert.Equal(0, result.IntegrationRisk);
    }

    [Fact]
    public async Task List_StaleIntegrationOverridesOtherwiseNormalSla()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        SeedShipment(db, Guid.NewGuid(), now, lastSyncAt: now.AddMinutes(-16), deadlineAt: now.AddHours(8), packedAt: null);
        await db.SaveChangesAsync();

        var result = await new SentinelService(db).ListAsync(Filter(), CancellationToken.None);
        var shipment = Assert.Single(result.Items);

        Assert.Equal("STALE", shipment.Freshness);
        Assert.Equal("STALE", shipment.RiskLevel);
        Assert.Equal("INTEGRATION_DIVERGENCE", shipment.Cause);
    }

    private static void SeedShipment(AppDbContext db, Guid clientId, DateTimeOffset now,
        DateTimeOffset lastSyncAt, DateTimeOffset deadlineAt, DateTimeOffset? packedAt)
    {
        const string tenantId = "tenant-sentinel";
        var shipmentId = $"SHIP-{Guid.NewGuid():N}";
        db.MarketplaceShipmentExternalStates.Add(new MarketplaceShipmentExternalState
        {
            TenantId = tenantId,
            ClientId = clientId,
            Provider = MarketplaceProvider.MercadoLivre,
            SellerId = 12345,
            ShipmentId = shipmentId,
            Status = "ready_to_ship",
            ProviderUpdatedAt = lastSyncAt,
            LastMarketplaceSyncAt = lastSyncAt,
            PayloadHash = "external-hash",
            Version = 1,
            CreatedAt = now.AddHours(-1),
            UpdatedAt = lastSyncAt
        });
        db.MarketplaceShipmentDispatchDeadlineVersions.Add(new MarketplaceShipmentDispatchDeadlineVersion
        {
            TenantId = tenantId,
            ClientId = clientId,
            Provider = MarketplaceProvider.MercadoLivre,
            SellerId = 12345,
            ShipmentId = shipmentId,
            DispatchDeadline = deadlineAt,
            Source = $"/shipments/{shipmentId}/sla",
            QueriedAt = lastSyncAt,
            Version = 1,
            PayloadHash = "deadline-hash",
            IsCurrent = true
        });
        if (packedAt.HasValue)
        {
            db.MarketplaceShipmentOperationalStates.Add(new MarketplaceShipmentOperationalState
            {
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = 12345,
                ShipmentId = shipmentId,
                PackedAt = packedAt,
                PackedBy = "operator-1",
                Version = 1
            });
        }
    }

    private static SentinelShipmentFilter Filter() => new(null, null, null, null, null, null, null, null, null, null, 0, 100);

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"sentinel-{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options);
    }
}
