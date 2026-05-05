using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Options;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class MercadoLivreSyncService
{
    private readonly IAppDbContext _dbContext;
    private readonly IMercadoLivreApiClient _mercadoLivreApiClient;
    private readonly MercadoLivreOAuthService _oauthService;
    private readonly StockAvailabilityService _stockAvailabilityService;
    private readonly MarketplaceAuditLogService _auditLogService;
    private readonly MercadoLivreOptions _options;
    private readonly ILogger<MercadoLivreSyncService> _logger;

    public MercadoLivreSyncService(
        IAppDbContext dbContext,
        IMercadoLivreApiClient mercadoLivreApiClient,
        MercadoLivreOAuthService oauthService,
        StockAvailabilityService stockAvailabilityService,
        MarketplaceAuditLogService auditLogService,
        IOptions<MercadoLivreOptions> options,
        ILogger<MercadoLivreSyncService> logger)
    {
        _dbContext = dbContext;
        _mercadoLivreApiClient = mercadoLivreApiClient;
        _oauthService = oauthService;
        _stockAvailabilityService = stockAvailabilityService;
        _auditLogService = auditLogService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ServiceResult<MercadoLivreSyncNowResult>> SyncNowAsync(
        string tenantId,
        Guid clientId,
        string? sellerId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty)
        {
            return ServiceResult<MercadoLivreSyncNowResult>.Failure(new[]
            {
                new ValidationError("context", "Invalid tenant/client context")
            });
        }

        if (!MercadoLivreSellerIdParser.TryParseOptional(sellerId, out var normalizedSeller))
        {
            return ServiceResult<MercadoLivreSyncNowResult>.Failure(new[]
            {
                new ValidationError("sellerId", "SellerId must be numeric")
            });
        }

        var connectionsQuery = _dbContext.TenantMarketplaceConnections
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == MarketplaceProvider.MercadoLivre);
        if (normalizedSeller.HasValue)
        {
            connectionsQuery = connectionsQuery.Where(item => item.SellerId == normalizedSeller.Value);
        }

        var connections = await connectionsQuery.ToListAsync(cancellationToken);
        if (connections.Count == 0)
        {
            return ServiceResult<MercadoLivreSyncNowResult>.Failure(new[]
            {
                new ValidationError("sellerId", "No active Mercado Livre connection found")
            });
        }

        var aggregate = new MercadoLivreSyncNowResult();
        foreach (var connection in connections)
        {
            var connectionResult = await SyncConnectionAsync(
                connection,
                Math.Max(1, _options.SyncLookbackDays),
                cancellationToken);
            aggregate.OrdersUpserted += connectionResult.OrdersUpserted;
            aggregate.ItemsUpserted += connectionResult.ItemsUpserted;
            aggregate.ReservationsCreated += connectionResult.ReservationsCreated;
        }

        return ServiceResult<MercadoLivreSyncNowResult>.Success(aggregate);
    }

    public async Task SyncAllConnectionsAsync(
        int? lookbackDaysOverride = null,
        CancellationToken cancellationToken = default)
    {
        var connections = await _dbContext.TenantMarketplaceConnections
            .Where(item => item.Provider == MarketplaceProvider.MercadoLivre)
            .ToListAsync(cancellationToken);
        var lookback = Math.Max(1, lookbackDaysOverride ?? _options.SyncLookbackDays);

        foreach (var connection in connections)
        {
            try
            {
                await SyncConnectionAsync(connection, lookback, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Marketplace sync failed tenant={TenantId} client={ClientId} seller={SellerId}",
                    connection.TenantId,
                    connection.ClientId,
                    connection.SellerId);
            }
        }
    }

    public async Task<int> ExpireReservationsAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var expired = await _dbContext.StockReservations
            .Where(item => item.Status == StockReservationStatus.Reserved
                           && item.ExpiresAt.HasValue
                           && item.ExpiresAt.Value < nowUtc)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
        {
            return 0;
        }

        var changedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reservation in expired)
        {
            reservation.Status = StockReservationStatus.Released;
            reservation.UpdatedAt = nowUtc;

            var item = await _dbContext.MarketplaceOrderItems.FirstOrDefaultAsync(
                i => i.Id == reservation.MarketplaceOrderItemId,
                cancellationToken);
            if (item != null)
            {
                item.ReservedQuantity = Math.Max(0, item.ReservedQuantity - reservation.Quantity);
                item.UpdatedAt = nowUtc;
            }

            var variant = await _dbContext.ProductVariants.FirstOrDefaultAsync(
                v => v.VariantSku == reservation.SabrVariantSku,
                cancellationToken);
            if (variant != null)
            {
                variant.ReservedStock = Math.Max(0, variant.ReservedStock - reservation.Quantity);
                variant.AvailableStock = StockAvailabilityService.ComputeAvailable(variant);
                changedKeys.Add($"{reservation.TenantId}|{reservation.ClientId}|{variant.VariantSku}");
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        foreach (var key in changedKeys)
        {
            var parts = key.Split('|');
            if (parts.Length != 3 || !Guid.TryParse(parts[1], out var clientId))
            {
                continue;
            }

            await _stockAvailabilityService.SyncStockForSkuAsync(parts[0], clientId, parts[2], cancellationToken);
        }

        return expired.Count;
    }

    private async Task<MercadoLivreSyncNowResult> SyncConnectionAsync(
        TenantMarketplaceConnection connection,
        int lookbackDays,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var fromUtc = nowUtc.AddDays(-Math.Max(1, lookbackDays));
        var accessToken = await _oauthService.GetValidAccessTokenAsync(connection, cancellationToken);
        var orderIds = await _mercadoLivreApiClient.SearchOrdersAsync(
            MercadoLivreSellerIdParser.ToApiString(connection.SellerId),
            fromUtc,
            nowUtc,
            accessToken,
            cancellationToken);

        var mappings = await _dbContext.TenantMarketplaceListingMaps
            .Where(item => item.TenantId == connection.TenantId
                           && item.ClientId == connection.ClientId
                           && item.Provider == MarketplaceProvider.MercadoLivre
                           && item.SellerId == connection.SellerId
                           && (!item.IntegrationId.HasValue || item.IntegrationId == connection.Id))
            .ToListAsync(cancellationToken);

        var mappingByIntegrationExactKey = mappings
            .Where(item => item.IntegrationId == connection.Id)
            .ToDictionary(
            item => BuildItemKey(item.MlItemId, item.MlVariationId),
            item => item,
            StringComparer.Ordinal);
        var fallbackMappingByExactKey = mappings
            .Where(item => !item.IntegrationId.HasValue)
            .ToDictionary(
                item => BuildItemKey(item.MlItemId, item.MlVariationId),
                item => item,
                StringComparer.Ordinal);

        var result = new MercadoLivreSyncNowResult();
        var changedSkus = new HashSet<string>(StringComparer.Ordinal);
        foreach (var orderId in orderIds.Distinct(StringComparer.Ordinal))
        {
            var details = await _mercadoLivreApiClient.GetOrderAsync(orderId, accessToken, cancellationToken);
            if (details == null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(details.ShipmentId))
            {
                MercadoLivreShipmentDetails? shipmentDetails = null;
                var shipment = await _mercadoLivreApiClient.GetShipmentAsync(details.ShipmentId, accessToken, cancellationToken);
                if (shipment != null)
                {
                    shipmentDetails = shipment;
                    details.ShippingMode = shipment.ShippingMode ?? details.ShippingMode;
                    details.LogisticType = shipment.LogisticType ?? details.LogisticType;
                    details.ShipByDeadlineAt ??= shipment.ShipByDeadlineAt;
                }

                await UpsertShipmentAsync(connection, details, shipmentDetails, cancellationToken);
            }

            var upsertResult = await UpsertOrderAsync(
                connection,
                details,
                mappingByIntegrationExactKey,
                fallbackMappingByExactKey,
                changedSkus,
                cancellationToken);
            result.OrdersUpserted += upsertResult.orders;
            result.ItemsUpserted += upsertResult.items;
            result.ReservationsCreated += upsertResult.reservations;
        }

        connection.LastSyncAt = nowUtc;
        connection.UpdatedAt = nowUtc;
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (changedSkus.Count > 0)
        {
            await _stockAvailabilityService.SyncStockForSkusAsync(
                connection.TenantId,
                connection.ClientId,
                changedSkus,
                cancellationToken);
        }

        return result;
    }

    private async Task<(int orders, int items, int reservations)> UpsertOrderAsync(
        TenantMarketplaceConnection connection,
        MercadoLivreOrderDetails details,
        IReadOnlyDictionary<string, TenantMarketplaceListingMap> mappingByIntegrationExactKey,
        IReadOnlyDictionary<string, TenantMarketplaceListingMap> fallbackMappingByExactKey,
        ISet<string> changedSkus,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;

        var order = await _dbContext.MarketplaceOrders
            .Include(item => item.Items)
            .FirstOrDefaultAsync(
                item => item.TenantId == connection.TenantId
                        && item.ClientId == connection.ClientId
                        && item.Provider == MarketplaceProvider.MercadoLivre
                        && item.MlOrderId == details.MlOrderId,
                cancellationToken);

        var createdOrder = false;
        var previousStatus = order?.Status;
        if (order == null)
        {
            order = new MarketplaceOrder
            {
                TenantId = connection.TenantId,
                ClientId = connection.ClientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = connection.SellerId,
                MlOrderId = details.MlOrderId,
                ImportedAt = nowUtc
            };
            _dbContext.MarketplaceOrders.Add(order);
            createdOrder = true;
        }

        order.Status = details.Status;
        order.PaidAt = details.PaidAt;
        order.ShipmentId = details.ShipmentId;
        order.ShippingMode = details.ShippingMode;
        order.LogisticType = details.LogisticType;
        order.ShipByDeadlineAt = details.ShipByDeadlineAt;
        order.RawJson = details.RawJson;
        order.UpdatedAt = nowUtc;

        var existingItems = await _dbContext.MarketplaceOrderItems
            .Where(item => item.MarketplaceOrderId == order.Id)
            .ToListAsync(cancellationToken);
        var existingByKey = existingItems.ToDictionary(
            item => BuildItemKey(item.MlItemId, item.MlVariationId),
            item => item,
            StringComparer.Ordinal);

        var reservationsCreated = 0;
        var itemsTouched = 0;
        foreach (var incomingItem in details.Items)
        {
            var itemKey = BuildItemKey(incomingItem.MlItemId, incomingItem.MlVariationId);
            if (!existingByKey.TryGetValue(itemKey, out var orderItem))
            {
                orderItem = new MarketplaceOrderItem
                {
                    MarketplaceOrderId = order.Id,
                    TenantId = connection.TenantId,
                    ClientId = connection.ClientId,
                    Provider = MarketplaceProvider.MercadoLivre,
                    SellerId = connection.SellerId,
                    MlItemId = incomingItem.MlItemId,
                    MlVariationId = incomingItem.MlVariationId
                };
                _dbContext.MarketplaceOrderItems.Add(orderItem);
                existingByKey[itemKey] = orderItem;
            }

            var mapping = ResolveMapping(
                mappingByIntegrationExactKey,
                fallbackMappingByExactKey,
                incomingItem.MlItemId,
                incomingItem.MlVariationId);
            orderItem.Quantity = incomingItem.Quantity;
            orderItem.MappingState = mapping == null ? MarketplaceMappingStates.Unmapped : MarketplaceMappingStates.Mapped;
            orderItem.SabrVariantSku = mapping?.SabrVariantSku;
            orderItem.RawJson = incomingItem.RawJson;
            orderItem.UpdatedAt = nowUtc;
            itemsTouched++;

            if (mapping == null)
            {
                continue;
            }

            var desiredReservation = incomingItem.Quantity;
            var currentReservation = Math.Max(0, orderItem.ReservedQuantity);
            var delta = desiredReservation - currentReservation;
            if (delta > 0)
            {
                var reservation = new StockReservation
                {
                    TenantId = connection.TenantId,
                    ClientId = connection.ClientId,
                    SabrVariantSku = mapping.SabrVariantSku,
                    MarketplaceOrderId = order.Id,
                    MarketplaceOrderItemId = orderItem.Id,
                    Quantity = delta,
                    Status = StockReservationStatus.Reserved,
                    ReservedAt = nowUtc,
                    ExpiresAt = nowUtc.AddHours(Math.Max(1, _options.ReservationTtlHours))
                };
                _dbContext.StockReservations.Add(reservation);
                reservationsCreated += 1;
                orderItem.ReservedQuantity = currentReservation + delta;

                var variant = await _dbContext.ProductVariants.FirstOrDefaultAsync(
                    item => item.VariantSku == mapping.SabrVariantSku,
                    cancellationToken);
                if (variant != null)
                {
                    variant.ReservedStock += delta;
                    variant.AvailableStock = StockAvailabilityService.ComputeAvailable(variant);
                    changedSkus.Add(variant.VariantSku);
                }
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (createdOrder)
        {
            await _auditLogService.RecordAsync(
                connection.TenantId,
                connection.ClientId,
                MarketplaceProvider.MercadoLivre,
                connection.SellerId,
                MarketplaceEventTopics.AuditOrderCreated,
                details.MlOrderId,
                new
                {
                    orderId = details.MlOrderId,
                    status = details.Status,
                    importedAt = nowUtc
                },
                "v1",
                cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        if (!string.Equals(previousStatus, details.Status, StringComparison.OrdinalIgnoreCase)
            && IsCancelledStatus(details.Status))
        {
            await _auditLogService.RecordAsync(
                connection.TenantId,
                connection.ClientId,
                MarketplaceProvider.MercadoLivre,
                connection.SellerId,
                MarketplaceEventTopics.AuditOrderCancelled,
                details.MlOrderId,
                new
                {
                    orderId = details.MlOrderId,
                    status = details.Status,
                    updatedAt = nowUtc
                },
                "v1",
                cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return (createdOrder ? 1 : 0, itemsTouched, reservationsCreated);
    }

    private async Task UpsertShipmentAsync(
        TenantMarketplaceConnection connection,
        MercadoLivreOrderDetails details,
        MercadoLivreShipmentDetails? shipmentDetails,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(details.ShipmentId))
        {
            return;
        }

        var shipment = await _dbContext.MarketplaceShipments.FirstOrDefaultAsync(
            item => item.TenantId == connection.TenantId
                    && item.ClientId == connection.ClientId
                    && item.Provider == MarketplaceProvider.MercadoLivre
                    && item.ShipmentId == details.ShipmentId,
            cancellationToken);
        if (shipment == null)
        {
            shipment = new MarketplaceShipment
            {
                TenantId = connection.TenantId,
                ClientId = connection.ClientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = connection.SellerId,
                ShipmentId = details.ShipmentId
            };
            _dbContext.MarketplaceShipments.Add(shipment);
        }

        shipment.MlOrderId = details.MlOrderId;
        shipment.Status = shipmentDetails?.Status ?? details.Status;
        shipment.Substatus = shipmentDetails?.Substatus;
        shipment.ShippingMode = details.ShippingMode;
        shipment.LogisticType = details.LogisticType;
        shipment.TrackingNumber = shipmentDetails?.TrackingNumber;
        shipment.TrackingMethod = shipmentDetails?.TrackingMethod;
        shipment.TrackingUrl = shipmentDetails?.TrackingUrl;
        shipment.ShippedAt = shipmentDetails?.ShippedAt;
        shipment.ShipByDeadlineAt = details.ShipByDeadlineAt;
        shipment.UpdatedAt = DateTimeOffset.UtcNow;

        var order = await _dbContext.MarketplaceOrders.FirstOrDefaultAsync(
            item => item.TenantId == connection.TenantId
                    && item.ClientId == connection.ClientId
                    && item.Provider == MarketplaceProvider.MercadoLivre
                    && item.MlOrderId == details.MlOrderId,
            cancellationToken);
        if (order != null && IsShippedStatus(shipment.Status))
        {
            order.Status = "shipped";
            order.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static bool IsShippedStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        var normalized = status.Trim().ToLowerInvariant();
        return normalized is "shipped" or "delivered";
    }

    private static bool IsCancelledStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        var normalized = status.Trim().ToLowerInvariant();
        return normalized is "cancelled" or "canceled";
    }

    private static string BuildItemKey(string itemId, string? variationId)
    {
        return $"{itemId.Trim()}|{(variationId ?? string.Empty).Trim()}";
    }

    private static TenantMarketplaceListingMap? ResolveMapping(
        IReadOnlyDictionary<string, TenantMarketplaceListingMap> mappingsByIntegration,
        IReadOnlyDictionary<string, TenantMarketplaceListingMap> fallbackMappings,
        string itemId,
        string? variationId)
    {
        var exactKey = BuildItemKey(itemId, variationId);
        if (mappingsByIntegration.TryGetValue(exactKey, out var exact))
        {
            return exact;
        }

        if (fallbackMappings.TryGetValue(exactKey, out var exactFallback))
        {
            return exactFallback;
        }

        var fallbackKey = BuildItemKey(itemId, null);
        if (mappingsByIntegration.TryGetValue(fallbackKey, out var fallback))
        {
            return fallback;
        }

        if (fallbackMappings.TryGetValue(fallbackKey, out var fallbackNullIntegration))
        {
            return fallbackNullIntegration;
        }

        return null;
    }
}
