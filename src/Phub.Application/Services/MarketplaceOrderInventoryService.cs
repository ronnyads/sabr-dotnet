using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class MarketplaceOrderInventoryService
{
    private readonly IAppDbContext _dbContext;

    public MarketplaceOrderInventoryService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<MarketplaceOrderInventorySummary> BuildSummaryAsync(
        MarketplaceOrder order,
        IReadOnlyCollection<MarketplaceShipment> shipments,
        CancellationToken cancellationToken = default)
    {
        var itemSummaries = await BuildItemSummariesAsync(order.Items.ToList(), cancellationToken);
        return BuildSummary(order, shipments, itemSummaries);
    }

    public async Task<List<MarketplaceOrderItemInventorySummary>> BuildItemSummariesAsync(
        IReadOnlyCollection<MarketplaceOrderItem> items,
        CancellationToken cancellationToken = default)
    {
        var variantSkus = items
            .Where(item => !string.IsNullOrWhiteSpace(item.SabrVariantSku))
            .Select(item => item.SabrVariantSku!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var variants = variantSkus.Count == 0
            ? new Dictionary<string, ProductVariant>(StringComparer.Ordinal)
            : await _dbContext.ProductVariants
                .AsNoTracking()
                .Where(item => variantSkus.Contains(item.VariantSku))
                .ToDictionaryAsync(item => item.VariantSku, StringComparer.Ordinal, cancellationToken);

        return items
            .Select(item => BuildItemSummary(item, variants.GetValueOrDefault(item.SabrVariantSku ?? string.Empty)))
            .ToList();
    }

    public static MarketplaceOrderInventorySummary BuildSummary(
        MarketplaceOrder order,
        IReadOnlyCollection<MarketplaceShipment> shipments,
        IReadOnlyCollection<MarketplaceOrderItemInventorySummary> itemSummaries)
    {
        var blockers = new HashSet<string>(StringComparer.Ordinal);
        if (itemSummaries.Count == 0)
        {
            blockers.Add(MarketplaceOrderPaymentBlockers.NoImportedItems);
        }

        if (itemSummaries.Any(item => MarketplaceMappingStates.IsUnmapped(item.MappingState)))
        {
            blockers.Add(MarketplaceOrderPaymentBlockers.UnmappedItem);
        }

        if (itemSummaries.Any(item => item.MissingQuantity > 0))
        {
            blockers.Add(MarketplaceOrderPaymentBlockers.OutOfStock);
        }

        if (MarketplaceOrderWorkflow.RequiresLabelForPayment(order.Provider)
            && !MarketplaceOrderWorkflow.HasOperationalLabel(shipments))
        {
            blockers.Add(MarketplaceOrderPaymentBlockers.LabelMissing);
        }

        if (string.Equals(order.CancellationRequestStatus, MarketplaceCancellationRequestStatuses.Requested, StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add(MarketplaceOrderPaymentBlockers.CancellationPending);
        }

        var inventoryStatus = ResolveInventoryStatus(itemSummaries);

        return new MarketplaceOrderInventorySummary(
            inventoryStatus,
            blockers.OrderBy(item => item, StringComparer.Ordinal).ToList(),
            blockers.Count == 0,
            blockers.Count == 0
            && order.SabrPaymentConfirmedAt.HasValue
            && !string.Equals(order.Status, MarketplaceOrderStatuses.Cancelled, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(order.Status, MarketplaceOrderStatuses.Refunded, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(order.Status, MarketplaceOrderStatuses.Delivered, StringComparison.OrdinalIgnoreCase),
            itemSummaries.ToList());
    }

    public async Task ReconcileReservationsAsync(
        MarketplaceOrder order,
        long sellerId,
        int reservationTtlHours,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var items = order.Items.ToList();
        var itemIds = items.Select(item => item.Id).ToList();
        var reservations = itemIds.Count == 0
            ? new List<StockReservation>()
            : await _dbContext.StockReservations
                .Where(item => item.MarketplaceOrderId == order.Id
                               && item.TenantId == order.TenantId
                               && item.ClientId == order.ClientId
                               && item.Status == StockReservationStatus.Reserved)
                .OrderBy(item => item.ReservedAt)
                .ToListAsync(cancellationToken);

        var reservationsByItemId = reservations
            .GroupBy(item => item.MarketplaceOrderItemId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var variantSkus = items
            .Where(item => !string.IsNullOrWhiteSpace(item.SabrVariantSku))
            .Select(item => item.SabrVariantSku!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var variants = variantSkus.Count == 0
            ? new Dictionary<string, ProductVariant>(StringComparer.Ordinal)
            : await _dbContext.ProductVariants
                .Where(item => variantSkus.Contains(item.VariantSku))
                .ToDictionaryAsync(item => item.VariantSku, StringComparer.Ordinal, cancellationToken);

        foreach (var item in items)
        {
            reservationsByItemId.TryGetValue(item.Id, out var currentReservations);
            currentReservations ??= [];

            if (currentReservations.Count > 0)
            {
                var normalizedItemSku = string.IsNullOrWhiteSpace(item.SabrVariantSku)
                    ? null
                    : item.SabrVariantSku.Trim();
                var skuChanged = currentReservations.Any(reservation =>
                    !string.Equals(reservation.SabrVariantSku, normalizedItemSku, StringComparison.Ordinal));

                if (skuChanged)
                {
                    foreach (var reservation in currentReservations)
                    {
                        if (!string.IsNullOrWhiteSpace(reservation.SabrVariantSku)
                            && !variants.TryGetValue(reservation.SabrVariantSku, out var reservedVariant))
                        {
                            reservedVariant = await _dbContext.ProductVariants.FirstOrDefaultAsync(
                                variant => variant.VariantSku == reservation.SabrVariantSku,
                                cancellationToken);
                            if (reservedVariant != null)
                            {
                                variants[reservedVariant.VariantSku] = reservedVariant;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(reservation.SabrVariantSku)
                            && variants.TryGetValue(reservation.SabrVariantSku, out var variantToRelease))
                        {
                            variantToRelease.ReservedStock = Math.Max(0, variantToRelease.ReservedStock - reservation.Quantity);
                            variantToRelease.AvailableStock = StockAvailabilityService.ComputeAvailable(variantToRelease);
                        }

                        reservation.Status = StockReservationStatus.Released;
                        reservation.Quantity = 0;
                        reservation.UpdatedAt = nowUtc;
                    }

                    currentReservations = [];
                }
            }

            var desiredReservation = MarketplaceMappingStates.IsMapped(item.MappingState)
                                     && !string.IsNullOrWhiteSpace(item.SabrVariantSku)
                ? Math.Max(0, item.Quantity)
                : 0;
            var currentReserved = currentReservations.Sum(entry => entry.Quantity);
            var delta = desiredReservation - currentReserved;

            if (delta > 0 && !string.IsNullOrWhiteSpace(item.SabrVariantSku))
            {
                _dbContext.StockReservations.Add(new StockReservation
                {
                    TenantId = order.TenantId,
                    ClientId = order.ClientId,
                    SabrVariantSku = item.SabrVariantSku!,
                    MarketplaceOrderId = order.Id,
                    MarketplaceOrderItemId = item.Id,
                    Quantity = delta,
                    Status = StockReservationStatus.Reserved,
                    ReservedAt = nowUtc,
                    ExpiresAt = nowUtc.AddHours(Math.Max(1, reservationTtlHours))
                });

                item.ReservedQuantity = currentReserved + delta;

                if (variants.TryGetValue(item.SabrVariantSku!, out var variant))
                {
                    variant.ReservedStock += delta;
                    variant.AvailableStock = StockAvailabilityService.ComputeAvailable(variant);
                }
            }
            else if (delta < 0)
            {
                var releaseRemaining = -delta;
                foreach (var reservation in currentReservations)
                {
                    if (releaseRemaining <= 0)
                    {
                        break;
                    }

                    var released = Math.Min(releaseRemaining, reservation.Quantity);
                    reservation.Quantity -= released;
                    reservation.UpdatedAt = nowUtc;
                    releaseRemaining -= released;

                    if (reservation.Quantity == 0)
                    {
                        reservation.Status = StockReservationStatus.Released;
                    }

                    if (!string.IsNullOrWhiteSpace(reservation.SabrVariantSku)
                        && variants.TryGetValue(reservation.SabrVariantSku, out var variant))
                    {
                        variant.ReservedStock = Math.Max(0, variant.ReservedStock - released);
                        variant.AvailableStock = StockAvailabilityService.ComputeAvailable(variant);
                    }
                }

                item.ReservedQuantity = desiredReservation;
            }
            else
            {
                item.ReservedQuantity = currentReserved;
            }

            item.SellerId = sellerId;
            item.UpdatedAt = nowUtc;
        }
    }

    private static MarketplaceOrderItemInventorySummary BuildItemSummary(MarketplaceOrderItem item, ProductVariant? variant)
    {
        if (!MarketplaceMappingStates.IsMapped(item.MappingState)
            || string.IsNullOrWhiteSpace(item.SabrVariantSku))
        {
            return new MarketplaceOrderItemInventorySummary(
                item.Id,
                item.MappingState,
                null,
                0,
                MarketplaceOrderItemStockStatuses.Unmapped);
        }

        if (variant == null)
        {
            return new MarketplaceOrderItemInventorySummary(
                item.Id,
                item.MappingState,
                null,
                Math.Max(0, item.Quantity),
                MarketplaceOrderItemStockStatuses.OutOfStock);
        }

        var reservedForOthers = Math.Max(0, variant.ReservedStock - Math.Max(0, item.ReservedQuantity));
        var availableForThisOrder = Math.Max(0, variant.PhysicalStock - reservedForOthers);
        var missingQuantity = Math.Max(0, item.Quantity - availableForThisOrder);
        var availableQuantity = Math.Max(0, item.Quantity - missingQuantity);

        var stockStatus = missingQuantity == 0
            ? MarketplaceOrderItemStockStatuses.InStock
            : availableQuantity == 0
                ? MarketplaceOrderItemStockStatuses.OutOfStock
                : MarketplaceOrderItemStockStatuses.Partial;

        return new MarketplaceOrderItemInventorySummary(
            item.Id,
            item.MappingState,
            variant.AvailableStock,
            missingQuantity,
            stockStatus);
    }

    private static string ResolveInventoryStatus(IReadOnlyCollection<MarketplaceOrderItemInventorySummary> itemSummaries)
    {
        if (itemSummaries.Count == 0)
        {
            return MarketplaceOrderInventoryStatuses.NoImportedItems;
        }

        if (itemSummaries.Any(item => item.StockStatus == MarketplaceOrderItemStockStatuses.Unmapped))
        {
            return MarketplaceOrderInventoryStatuses.Unmapped;
        }

        var withMissing = itemSummaries.Where(item => item.MissingQuantity > 0).ToList();
        if (withMissing.Count == 0)
        {
            return MarketplaceOrderInventoryStatuses.MappedInStock;
        }

        return withMissing.Count == itemSummaries.Count
               && withMissing.All(item => item.StockStatus == MarketplaceOrderItemStockStatuses.OutOfStock)
            ? MarketplaceOrderInventoryStatuses.OutOfStock
            : MarketplaceOrderInventoryStatuses.MappedPartialStock;
    }
}

public sealed record MarketplaceOrderInventorySummary(
    string InventoryStatus,
    List<string> PaymentBlockers,
    bool CanMarkPaid,
    bool CanEnterFulfillment,
    List<MarketplaceOrderItemInventorySummary> Items);

public sealed record MarketplaceOrderItemInventorySummary(
    Guid OrderItemId,
    string? MappingState,
    int? AvailableStock,
    int MissingQuantity,
    string StockStatus);
