using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Domain.Entities;

namespace Phub.Application.Services;

public sealed class StockReservationAllocationService
{
    private readonly IAppDbContext _db;
    public StockReservationAllocationService(IAppDbContext db) => _db = db;

    public async Task<bool> AllocateAsync(StockReservation reservation, ProductVariant variant, long sellerId,
        CancellationToken cancellationToken = default)
    {
        var needed = reservation.Quantity;
        var lots = await _db.SellerOwnedStockLots
            .Where(x => x.TenantId == reservation.TenantId && x.ClientId == reservation.ClientId
                && x.SellerId == sellerId && x.VariantSku == reservation.SabrVariantSku && x.AvailableQuantity > 0)
            .OrderBy(x => x.AcquiredAt).ThenBy(x => x.Id).ToListAsync(cancellationToken);

        var allocations = new List<StockReservationAllocation>();
        foreach (var lot in lots)
        {
            if (needed == 0) break;
            var take = Math.Min(needed, lot.AvailableQuantity);
            lot.AvailableQuantity -= take;
            lot.ReservedQuantity += take;
            allocations.Add(new StockReservationAllocation
            {
                StockReservationId = reservation.Id, SellerOwnedStockLotId = lot.Id,
                Source = StockReservationSources.PrePurchasedLot, Quantity = take,
                UnitCostCents = lot.UnitCostCents, CurrencyId = lot.CurrencyId
            });
            needed -= take;
        }

        if (needed > 0)
        {
            if (StockAvailabilityService.ComputeAvailable(variant) < needed)
            {
                foreach (var allocation in allocations)
                {
                    var lot = lots.Single(x => x.Id == allocation.SellerOwnedStockLotId);
                    lot.AvailableQuantity += allocation.Quantity;
                    lot.ReservedQuantity -= allocation.Quantity;
                }
                return false;
            }
            allocations.Add(new StockReservationAllocation
            {
                StockReservationId = reservation.Id, Source = StockReservationSources.GeneralStock,
                Quantity = needed, CurrencyId = "BRL"
            });
            variant.ReservedStock += needed;
        }

        reservation.Source = allocations.Select(x => x.Source).Distinct().Count() > 1
            ? StockReservationSources.Mixed : allocations[0].Source;
        _db.StockReservationAllocations.AddRange(allocations);
        variant.AvailableStock = StockAvailabilityService.ComputeAvailable(variant);
        return true;
    }

    public async Task ReleaseAsync(StockReservation reservation, ProductVariant variant,
        CancellationToken cancellationToken = default)
    {
        var allocations = await _db.StockReservationAllocations
            .Where(x => x.StockReservationId == reservation.Id && x.ConsumedAt == null).ToListAsync(cancellationToken);
        if (allocations.Count == 0)
        {
            variant.ReservedStock = Math.Max(0, variant.ReservedStock - reservation.Quantity);
            variant.AvailableStock = StockAvailabilityService.ComputeAvailable(variant);
            return;
        }
        var lotIds = allocations.Where(x => x.SellerOwnedStockLotId.HasValue).Select(x => x.SellerOwnedStockLotId!.Value).ToList();
        var lots = await _db.SellerOwnedStockLots.Where(x => lotIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        foreach (var allocation in allocations)
        {
            if (allocation.Source == StockReservationSources.PrePurchasedLot && allocation.SellerOwnedStockLotId.HasValue)
            {
                var lot = lots[allocation.SellerOwnedStockLotId.Value];
                lot.ReservedQuantity -= allocation.Quantity;
                lot.AvailableQuantity += allocation.Quantity;
            }
            else variant.ReservedStock = Math.Max(0, variant.ReservedStock - allocation.Quantity);
        }
        variant.AvailableStock = StockAvailabilityService.ComputeAvailable(variant);
    }

    public async Task ConsumeAsync(StockReservation reservation, ProductVariant variant, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var allocations = await _db.StockReservationAllocations
            .Where(x => x.StockReservationId == reservation.Id && x.ConsumedAt == null).ToListAsync(cancellationToken);
        if (allocations.Count == 0)
        {
            variant.ReservedStock = Math.Max(0, variant.ReservedStock - reservation.Quantity);
            variant.PhysicalStock = Math.Max(0, variant.PhysicalStock - reservation.Quantity);
            reservation.Status = Phub.Domain.Enums.StockReservationStatus.Consumed;
            reservation.UpdatedAt = now;
            variant.AvailableStock = StockAvailabilityService.ComputeAvailable(variant);
            variant.InventoryVersion = checked(variant.InventoryVersion + 1);
            variant.UpdatedAt = now;
            return;
        }
        var lotIds = allocations.Where(x => x.SellerOwnedStockLotId.HasValue).Select(x => x.SellerOwnedStockLotId!.Value).ToList();
        var lots = await _db.SellerOwnedStockLots.Where(x => lotIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        foreach (var allocation in allocations)
        {
            if (allocation.Source == StockReservationSources.PrePurchasedLot && allocation.SellerOwnedStockLotId.HasValue)
            {
                var lot = lots[allocation.SellerOwnedStockLotId.Value];
                lot.ReservedQuantity -= allocation.Quantity;
                lot.ConsumedQuantity += allocation.Quantity;
                variant.ClientOwnedStock = Math.Max(0, variant.ClientOwnedStock - allocation.Quantity);
            }
            else variant.ReservedStock = Math.Max(0, variant.ReservedStock - allocation.Quantity);
            variant.PhysicalStock = Math.Max(0, variant.PhysicalStock - allocation.Quantity);
            allocation.ConsumedAt = now;
        }
        reservation.Status = Phub.Domain.Enums.StockReservationStatus.Consumed;
        reservation.UpdatedAt = now;
        variant.AvailableStock = StockAvailabilityService.ComputeAvailable(variant);
        variant.InventoryVersion = checked(variant.InventoryVersion + 1);
        variant.UpdatedAt = now;
    }
}
