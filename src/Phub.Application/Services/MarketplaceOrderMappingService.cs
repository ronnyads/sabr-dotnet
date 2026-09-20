using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Domain.ValueObjects;

namespace Phub.Application.Services;

public sealed class MarketplaceOrderMappingService
{
    private readonly IAppDbContext _dbContext;
    private readonly CatalogAuthorizationService _catalogAuthorizationService;
    private readonly MarketplaceOrderInventoryService _inventoryService;
    private readonly OperationalFinancialProjectionService _financialProjection;
    private readonly ILogger<MarketplaceOrderMappingService> _logger;

    public MarketplaceOrderMappingService(
        IAppDbContext dbContext,
        CatalogAuthorizationService catalogAuthorizationService,
        MarketplaceOrderInventoryService inventoryService,
        OperationalFinancialProjectionService financialProjection,
        ILogger<MarketplaceOrderMappingService> logger)
    {
        _dbContext = dbContext;
        _catalogAuthorizationService = catalogAuthorizationService;
        _inventoryService = inventoryService;
        _financialProjection = financialProjection;
        _logger = logger;
    }

    public async Task<ServiceResult<List<MarketplaceMappingListItemDto>>> ListMappingsAsync(
        string tenantId,
        Guid clientId,
        MarketplaceProvider provider,
        string? sellerId = null,
        Guid? integrationId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedSellerId = NormalizeSellerId(provider, sellerId);
        if (sellerId is not null && normalizedSellerId == null)
        {
            return ServiceResult<List<MarketplaceMappingListItemDto>>.Failure(
                ServiceErrorCodes.ValidationError,
                "sellerId",
                "SellerId invalido para este canal.");
        }

        var query = _dbContext.TenantMarketplaceListingMaps
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == provider);

        if (normalizedSellerId.HasValue)
        {
            query = query.Where(item => item.SellerId == normalizedSellerId.Value);
        }

        if (integrationId.HasValue)
        {
            query = query.Where(item => item.IntegrationId == integrationId.Value);
        }

        var mappings = await query
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.MlItemId)
            .ThenBy(item => item.MlVariationId)
            .ToListAsync(cancellationToken);

        var mappingKeys = mappings
            .Select(item => BuildLookupKey(item.SellerId, item.MlItemId, item.MlVariationId))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var orderItems = await _dbContext.MarketplaceOrderItems
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == provider)
            .ToListAsync(cancellationToken);

        var ordersAffectedByKey = orderItems
            .GroupBy(item => BuildLookupKey(item.SellerId, item.MlItemId, item.MlVariationId), StringComparer.Ordinal)
            .Where(group => mappingKeys.Contains(group.Key, StringComparer.Ordinal))
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.MarketplaceOrderId).Distinct().Count(),
                StringComparer.Ordinal);

        var variants = await LoadVariantLookupAsync(mappings.Select(item => item.SabrVariantSku), cancellationToken);
        var products = await LoadProductLookupAsync(variants.Values.Select(item => item.BaseSku), cancellationToken);

        var items = mappings.Select(item =>
        {
            variants.TryGetValue(item.SabrVariantSku, out var variant);
            var product = variant != null ? products.GetValueOrDefault(variant.BaseSku) : null;
            var channelMetadata = FindChannelMetadata(
                orderItems.FirstOrDefault(orderItem =>
                    orderItem.SellerId == item.SellerId
                    && string.Equals(orderItem.MlItemId, item.MlItemId, StringComparison.Ordinal)
                    && string.Equals(orderItem.MlVariationId, item.MlVariationId, StringComparison.Ordinal)));

            return new MarketplaceMappingListItemDto
            {
                Id = item.Id,
                Provider = item.Provider,
                IntegrationId = item.IntegrationId,
                SellerId = item.SellerId > 0 ? item.SellerId.ToString() : null,
                ExternalItemId = item.MlItemId,
                ExternalVariationId = item.MlVariationId,
                SabrVariantSku = item.SabrVariantSku,
                BaseSku = variant?.BaseSku,
                ProductName = product?.Name ?? channelMetadata.ProductName,
                VariantName = variant?.Name ?? channelMetadata.VariantName,
                ChannelSku = channelMetadata.ChannelSku,
                Action = "loaded",
                OrdersAffected = ordersAffectedByKey.GetValueOrDefault(BuildLookupKey(item.SellerId, item.MlItemId, item.MlVariationId)),
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt
            };
        }).ToList();

        return ServiceResult<List<MarketplaceMappingListItemDto>>.Success(items);
    }

    public async Task<ServiceResult<List<MarketplaceUnmappedItemDto>>> ListUnmappedItemsAsync(
        string tenantId,
        Guid clientId,
        MarketplaceProvider provider,
        string? sellerId = null,
        Guid? integrationId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedSellerId = NormalizeSellerId(provider, sellerId);
        if (sellerId is not null && normalizedSellerId == null)
        {
            return ServiceResult<List<MarketplaceUnmappedItemDto>>.Failure(
                ServiceErrorCodes.ValidationError,
                "sellerId",
                "SellerId invalido para este canal.");
        }

        var query = _dbContext.MarketplaceOrderItems
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == provider
                           && (string.IsNullOrWhiteSpace(item.SabrVariantSku)
                               || item.MappingState == MarketplaceMappingStates.Unmapped
                               || item.MappingState == MarketplaceMappingStates.UnmappedMissingChannelSku
                               || item.MappingState == MarketplaceMappingStates.UnmappedUnknownChannelSku
                               || item.MappingState == MarketplaceMappingStates.UnmappedAmbiguousChannelSku
                               || item.MappingState == MarketplaceMappingStates.UnmappedSkuNotAuthorized
                               || item.MappingState == MarketplaceMappingStates.UnmappedMappingNotAuthorized));

        if (normalizedSellerId.HasValue)
        {
            query = query.Where(item => item.SellerId == normalizedSellerId.Value);
        }

        var items = await query
            .Join(
                _dbContext.MarketplaceOrders.AsNoTracking(),
                item => item.MarketplaceOrderId,
                order => order.Id,
                (item, order) => new { item, order })
            .OrderByDescending(entry => entry.order.ImportedAt)
            .ToListAsync(cancellationToken);

        var grouped = items
            .GroupBy(
                entry => BuildGroupKey(provider, entry.item.SellerId, entry.item.MlItemId, entry.item.MlVariationId),
                StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var metadata = FindChannelMetadata(first.item);
                var reason = ResolveMappingReason(first.item, metadata);
                return new MarketplaceUnmappedItemDto
                {
                    MappingKey = group.Key,
                    Provider = provider,
                    IntegrationId = integrationId,
                    SellerId = first.item.SellerId > 0 ? first.item.SellerId.ToString() : null,
                    ExternalItemId = first.item.MlItemId,
                    ExternalVariationId = NormalizeNullable(first.item.MlVariationId),
                    ChannelSku = metadata.ChannelSku,
                    ProductName = metadata.ProductName,
                    VariantName = metadata.VariantName,
                    ThumbnailUrl = metadata.ThumbnailUrl,
                    MappingReason = reason,
                    OrdersAffected = group.Select(entry => entry.order.Id).Distinct().Count(),
                    TotalUnits = group.Sum(entry => entry.item.Quantity),
                    LatestImportedAt = group.Max(entry => entry.order.ImportedAt)
                };
            })
            .OrderByDescending(item => item.LatestImportedAt)
            .ThenBy(item => item.ExternalItemId, StringComparer.Ordinal)
            .ToList();

        return ServiceResult<List<MarketplaceUnmappedItemDto>>.Success(grouped);
    }

    public async Task<ServiceResult<MarketplaceMappingListItemDto>> UpsertMappingAsync(
        string tenantId,
        Guid clientId,
        MarketplaceUpsertMappingRequest request,
        Guid actorUserId = default,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty)
        {
            return ServiceResult<MarketplaceMappingListItemDto>.Failure(
                ServiceErrorCodes.ValidationError,
                "context",
                "Contexto invalido para mapeamento.");
        }

        var normalizedItemId = NormalizeKey(request.ExternalItemId);
        if (string.IsNullOrWhiteSpace(normalizedItemId))
        {
            return ServiceResult<MarketplaceMappingListItemDto>.Failure(
                ServiceErrorCodes.ValidationError,
                "externalItemId",
                "O item externo e obrigatorio.");
        }

        var normalizedSellerId = NormalizeSellerId(request.Provider, request.SellerId);
        if (request.SellerId is not null && normalizedSellerId == null)
        {
            return ServiceResult<MarketplaceMappingListItemDto>.Failure(
                ServiceErrorCodes.ValidationError,
                "sellerId",
                "SellerId invalido para este canal.");
        }

        var selectedSku = NormalizeKey(request.SelectedCatalogSku);
        if (string.IsNullOrWhiteSpace(selectedSku))
        {
            return ServiceResult<MarketplaceMappingListItemDto>.Failure(
                ServiceErrorCodes.ValidationError,
                "selectedCatalogSku",
                "Selecione um produto ou variante do catalogo.");
        }

        var resolvedVariant = await ResolveSelectedCatalogSkuAsync(
            tenantId,
            clientId,
            selectedSku,
            cancellationToken);
        if (!resolvedVariant.Succeeded || resolvedVariant.Data == null)
        {
            return ServiceResult<MarketplaceMappingListItemDto>.Failure(
                resolvedVariant.ErrorCode ?? ServiceErrorCodes.ValidationError,
                resolvedVariant.Errors);
        }

        var connection = await ResolveConnectionAsync(
            tenantId,
            clientId,
            request.Provider,
            normalizedSellerId,
            request.IntegrationId,
            cancellationToken);
        if (!connection.Succeeded || connection.Data == null)
        {
            return ServiceResult<MarketplaceMappingListItemDto>.Failure(
                connection.ErrorCode ?? ServiceErrorCodes.ValidationError,
                connection.Errors);
        }

        var normalizedVariationId = NormalizeNullable(request.ExternalVariationId);
        var existing = await _dbContext.TenantMarketplaceListingMaps.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == request.Provider
                    && item.SellerId == connection.Data.SellerId
                    && item.MlItemId == normalizedItemId
                    && item.MlVariationId == normalizedVariationId,
            cancellationToken);

        var action = "created";
        if (existing == null)
        {
            existing = new TenantMarketplaceListingMap
            {
                TenantId = tenantId,
                ClientId = clientId,
                Provider = request.Provider,
                IntegrationId = connection.Data.Id,
                SellerId = connection.Data.SellerId,
                MlItemId = normalizedItemId,
                MlVariationId = normalizedVariationId,
                ChannelSku = NormalizeSku(request.SelectedCatalogSku),
                SabrVariantSku = resolvedVariant.Data.VariantSku,
                MappingVersion = 1
            };
            _dbContext.TenantMarketplaceListingMaps.Add(existing);
        }
        else if (string.Equals(existing.SabrVariantSku, resolvedVariant.Data.VariantSku, StringComparison.Ordinal))
        {
            action = "unchanged";
        }
        else
        {
            existing.IntegrationId = connection.Data.Id;
            existing.SellerId = connection.Data.SellerId;
            existing.SabrVariantSku = resolvedVariant.Data.VariantSku;
            existing.ChannelSku = NormalizeSku(request.SelectedCatalogSku);
            existing.MappingVersion++;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            action = "updated";
        }

        var product = await _dbContext.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Sku == resolvedVariant.Data.BaseSku, cancellationToken);

        var clientOwnsProduct = await _dbContext.Publications.AsNoTracking().AnyAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.ProductSku == resolvedVariant.Data.BaseSku
                    && item.Status == PublicationStatus.Draft,
            cancellationToken);
        if (!clientOwnsProduct && product != null)
        {
            var now = DateTimeOffset.UtcNow;
            _dbContext.Publications.Add(new Publication
            {
                TenantId = tenantId,
                ClientId = clientId,
                ProductSku = product.Sku,
                Status = PublicationStatus.Draft,
                PricingMode = PricingMode.CatalogPrice,
                CostPriceCentsSnapshot = product.CostPriceCents,
                CatalogPriceCentsSnapshot = product.CatalogPriceCents,
                FinalPriceCentsSnapshot = product.CatalogPriceCents,
                PriceSnapshotTakenAt = now,
                CreatedByUserId = actorUserId,
                UpdatedByUserId = actorUserId,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        _dbContext.AuditEvents.Add(new AuditEvent
        {
            TenantId = tenantId,
            ActorType = "TenantUser",
            ActorId = actorUserId == Guid.Empty ? null : actorUserId,
            Action = action == "updated" ? "MarketplaceMapping.RemapFutureOrders" : "MarketplaceMapping.Upsert",
            Entity = nameof(TenantMarketplaceListingMap),
            EntityId = existing.Id,
            RequestId = Guid.NewGuid(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                existing.Provider,
                existing.SellerId,
                existing.MlItemId,
                existing.MlVariationId,
                existing.UserProductId,
                existing.ChannelSku,
                existing.SabrVariantSku,
                existing.MappingVersion,
                action,
                addedToMyProducts = !clientOwnsProduct
            })
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        var ordersAffected = await ApplyMappingToPendingItemsAsync(existing, cancellationToken);

        var channelMetadata = new ChannelMetadata(existing.ChannelSku);

        return ServiceResult<MarketplaceMappingListItemDto>.Success(new MarketplaceMappingListItemDto
        {
            Id = existing.Id,
            Provider = existing.Provider,
            IntegrationId = existing.IntegrationId,
            SellerId = existing.SellerId > 0 ? existing.SellerId.ToString() : null,
            ExternalItemId = existing.MlItemId,
            ExternalVariationId = existing.MlVariationId,
            SabrVariantSku = existing.SabrVariantSku,
            BaseSku = resolvedVariant.Data.BaseSku,
            ProductName = product?.Name ?? channelMetadata.ProductName,
            VariantName = resolvedVariant.Data.Name,
            ChannelSku = channelMetadata.ChannelSku,
            Action = action,
            OrdersAffected = ordersAffected,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = existing.UpdatedAt
        });
    }

    public async Task<int> ApplyMappingToPendingItemsAsync(
        TenantMarketplaceListingMap mapping,
        CancellationToken cancellationToken = default)
    {
        // A new mapping resolves only items that are still pending. A resolved order item
        // is an immutable historical snapshot and is never rewritten by a later remap.
        var pendingItems = await _dbContext.MarketplaceOrderItems
            .Where(item => item.TenantId == mapping.TenantId
                           && item.ClientId == mapping.ClientId
                           && item.Provider == mapping.Provider
                           && item.SellerId == mapping.SellerId
                           && item.MlItemId == mapping.MlItemId
                           && item.MlVariationId == mapping.MlVariationId
                           && (item.SabrVariantSku == null
                               || item.MappingState == MarketplaceMappingStates.Unmapped
                               || item.MappingState == MarketplaceMappingStates.UnmappedMissingChannelSku
                               || item.MappingState == MarketplaceMappingStates.UnmappedUnknownChannelSku
                               || item.MappingState == MarketplaceMappingStates.UnmappedAmbiguousChannelSku
                               || item.MappingState == MarketplaceMappingStates.UnmappedSkuNotAuthorized
                               || item.MappingState == MarketplaceMappingStates.UnmappedMappingNotAuthorized))
            .ToListAsync(cancellationToken);
        if (pendingItems.Count == 0) return 0;

        var resolvedAt = DateTimeOffset.UtcNow;
        foreach (var item in pendingItems)
        {
            ApplyResolutionSnapshot(
                item,
                new MarketplaceItemResolutionResult(
                    mapping.SabrVariantSku,
                    MarketplaceMappingStates.MappedByListingMap,
                    MarketplaceMappingReasonCodes.MappedByListingMap,
                    item.ChannelSku,
                    "manual_listing_mapping",
                    mapping.Id,
                    mapping.MappingVersion),
                resolvedAt);
            item.UpdatedAt = resolvedAt;
        }

        var orderIds = pendingItems.Select(item => item.MarketplaceOrderId).Distinct().ToList();
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Mapping snapshots are durable before derived stock and financial projections.
        // Any later checkout repeats inventory validation transactionally.
        try
        {
            var pendingOrders = await _dbContext.MarketplaceOrders
                .Where(item => orderIds.Contains(item.Id))
                .ToListAsync(cancellationToken);
            var allOrderItems = await _dbContext.MarketplaceOrderItems
                .Where(item => orderIds.Contains(item.MarketplaceOrderId))
                .ToListAsync(cancellationToken);
            foreach (var pendingOrder in pendingOrders.Where(item => !item.SabrPaymentConfirmedAt.HasValue))
            {
                pendingOrder.Items = allOrderItems
                    .Where(item => item.MarketplaceOrderId == pendingOrder.Id)
                    .ToList();
                await _inventoryService.ReconcileReservationsAsync(
                    pendingOrder,
                    pendingOrder.SellerId,
                    reservationTtlHours: 24,
                    cancellationToken: cancellationToken);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            foreach (var orderId in orderIds)
            {
                await _financialProjection.ProjectOrderAsync(orderId, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Mapping {MappingId} was saved but derived refresh was deferred for {OrderCount} order(s).",
                mapping.Id,
                orderIds.Count);
        }

        return orderIds.Count;
    }

    public async Task<ServiceResult<MarketplaceMappingReanalysisResult>> ReanalyzePendingItemsAsync(
        string tenantId,
        Guid clientId,
        MarketplaceProvider provider,
        Guid actorUserId = default,
        CancellationToken cancellationToken = default)
    {
        var pendingItems = await _dbContext.MarketplaceOrderItems
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == provider
                           && (string.IsNullOrWhiteSpace(item.SabrVariantSku)
                               || item.MappingState == MarketplaceMappingStates.Unmapped
                               || item.MappingState == MarketplaceMappingStates.UnmappedMissingChannelSku
                               || item.MappingState == MarketplaceMappingStates.UnmappedUnknownChannelSku
                               || item.MappingState == MarketplaceMappingStates.UnmappedAmbiguousChannelSku
                               || item.MappingState == MarketplaceMappingStates.UnmappedSkuNotAuthorized
                               || item.MappingState == MarketplaceMappingStates.UnmappedMappingNotAuthorized))
            .OrderBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);

        if (pendingItems.Count == 0)
        {
            return ServiceResult<MarketplaceMappingReanalysisResult>.Success(new MarketplaceMappingReanalysisResult());
        }

        var connectionIds = await _dbContext.TenantMarketplaceConnections
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == provider)
            .ToDictionaryAsync(item => item.SellerId, item => (Guid?)item.Id, cancellationToken);

        var mappedItemCount = 0;
        var affectedOrderIds = new HashSet<Guid>();
        var releasedOrdersCount = 0;
        var groups = pendingItems.GroupBy(
            item => BuildGroupKey(provider, item.SellerId, item.MlItemId, item.MlVariationId),
            StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var first = group.First();
            var metadata = FindChannelMetadata(first);
            var resolution = await ResolveImportedItemAsync(
                tenantId,
                clientId,
                provider,
                first.SellerId,
                connectionIds.GetValueOrDefault(first.SellerId),
                first.MlItemId,
                first.MlVariationId,
                metadata.ChannelSku,
                cancellationToken);

            var resolvedAt = DateTimeOffset.UtcNow;
            foreach (var item in group)
            {
                item.ChannelSku = NormalizeNullable(item.ChannelSku) ?? metadata.ChannelSku;
                item.ProductName = NormalizeNullable(item.ProductName) ?? metadata.ProductName;
                ApplyResolutionSnapshot(item, resolution, resolvedAt);
                item.UpdatedAt = resolvedAt;
                if (!string.IsNullOrWhiteSpace(resolution.SabrVariantSku))
                {
                    mappedItemCount++;
                    affectedOrderIds.Add(item.MarketplaceOrderId);
                }
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (affectedOrderIds.Count > 0)
        {
            var pendingOrders = await _dbContext.MarketplaceOrders
                .Where(item => affectedOrderIds.Contains(item.Id) && !item.SabrPaymentConfirmedAt.HasValue)
                .ToListAsync(cancellationToken);
            var allOrderItems = await _dbContext.MarketplaceOrderItems
                .Where(item => affectedOrderIds.Contains(item.MarketplaceOrderId))
                .ToListAsync(cancellationToken);
            var readyOrderIds = allOrderItems
                .GroupBy(item => item.MarketplaceOrderId)
                .Where(group => group.All(item => !MarketplaceMappingStates.IsUnmapped(item.MappingState)
                                                 && !string.IsNullOrWhiteSpace(item.SabrVariantSku)))
                .Select(group => group.Key)
                .ToHashSet();
            releasedOrdersCount = pendingOrders.Count(item => readyOrderIds.Contains(item.Id));
            foreach (var pendingOrder in pendingOrders.Where(item => readyOrderIds.Contains(item.Id)))
            {
                pendingOrder.Items = allOrderItems
                    .Where(item => item.MarketplaceOrderId == pendingOrder.Id)
                    .ToList();
                await _inventoryService.ReconcileReservationsAsync(
                    pendingOrder,
                    pendingOrder.SellerId,
                    reservationTtlHours: 24,
                    cancellationToken: cancellationToken);
            }
            await _dbContext.SaveChangesAsync(cancellationToken);

            foreach (var orderId in affectedOrderIds)
            {
                await _financialProjection.ProjectOrderAsync(orderId, cancellationToken);
            }
        }

        _dbContext.AuditEvents.Add(new AuditEvent
        {
            TenantId = tenantId,
            ActorType = actorUserId == Guid.Empty ? "System" : "TenantUser",
            ActorId = actorUserId == Guid.Empty ? null : actorUserId,
            Action = "MarketplaceMapping.ReanalyzePending",
            Entity = nameof(MarketplaceOrderItem),
            RequestId = Guid.NewGuid(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                provider,
                itemsExamined = pendingItems.Count,
                itemsMapped = mappedItemCount,
                itemsRemaining = pendingItems.Count - mappedItemCount,
                ordersReleased = releasedOrdersCount
            })
        });
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<MarketplaceMappingReanalysisResult>.Success(new MarketplaceMappingReanalysisResult
        {
            ItemsExamined = pendingItems.Count,
            ItemsMapped = mappedItemCount,
            ItemsRemaining = pendingItems.Count - mappedItemCount,
            OrdersReleased = releasedOrdersCount
        });
    }

    public async Task<ServiceResult<bool>> DeleteMappingAsync(
        string tenantId,
        Guid clientId,
        Guid mappingId,
        CancellationToken cancellationToken = default)
    {
        var mapping = await _dbContext.TenantMarketplaceListingMaps.FirstOrDefaultAsync(
            item => item.Id == mappingId
                    && item.TenantId == tenantId
                    && item.ClientId == clientId,
            cancellationToken);

        if (mapping == null)
        {
            return ServiceResult<bool>.Failure(
                ServiceErrorCodes.NotFound,
                "mapping",
                "Mapeamento nao encontrado.");
        }

        _dbContext.TenantMarketplaceListingMaps.Remove(mapping);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    public async Task<MarketplaceItemResolutionResult> ResolveImportedItemAsync(
        string tenantId,
        Guid clientId,
        MarketplaceProvider provider,
        long sellerId,
        Guid? integrationId,
        string externalItemId,
        string? externalVariationId,
        string? channelSku,
        CancellationToken cancellationToken = default,
        Guid? ignoredMappingId = null)
    {
        var normalizedItemId = NormalizeKey(externalItemId);
        var normalizedVariationId = NormalizeNullable(externalVariationId);
        var normalizedChannelSku = NormalizeSku(channelSku);

        var manualMapping = await _dbContext.TenantMarketplaceListingMaps
            .AsNoTracking()
            .Where(
                item => item.TenantId == tenantId
                        && item.ClientId == clientId
                        && item.Provider == provider
                        && (!ignoredMappingId.HasValue || item.Id != ignoredMappingId.Value)
                        && item.SellerId == sellerId
                        && item.MlItemId == normalizedItemId
                        && item.MlVariationId == normalizedVariationId
                        && (integrationId.HasValue
                            ? item.IntegrationId == integrationId.Value || item.IntegrationId == null
                            : item.IntegrationId == null))
            .OrderByDescending(item => item.IntegrationId == integrationId)
            .FirstOrDefaultAsync(cancellationToken);

        if (manualMapping != null)
        {
            var mappedVariant = await _dbContext.ProductVariants
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.VariantSku == manualMapping.SabrVariantSku && item.IsActive, cancellationToken);
            if (mappedVariant != null)
            {
                var isAuthorized = await _catalogAuthorizationService.IsSkuAllowedAsync(
                    tenantId,
                    clientId,
                    mappedVariant.BaseSku,
                    cancellationToken);
                if (isAuthorized)
                {
                    return new MarketplaceItemResolutionResult(
                        mappedVariant.VariantSku,
                        MarketplaceMappingStates.MappedByListingMap,
                        MarketplaceMappingReasonCodes.MappedByListingMap,
                        normalizedChannelSku,
                        MarketplaceMappingReasonCodes.MappedByListingMap,
                        manualMapping.Id,
                        manualMapping.MappingVersion);
                }
            }

            return new MarketplaceItemResolutionResult(
                null,
                MarketplaceMappingStates.UnmappedMappingNotAuthorized,
                MarketplaceMappingReasonCodes.UnmappedMappedSkuNotAuthorized,
                normalizedChannelSku,
                MarketplaceMappingReasonCodes.UnmappedMappedSkuNotAuthorized);
        }

        if (!string.IsNullOrWhiteSpace(normalizedChannelSku))
        {
            // A SKU may identify an exact variant or a base product with one
            // active variant. Only one authorized candidate is safe to map.
            var skuCandidates = await _dbContext.ProductVariants
                .AsNoTracking()
                .Where(item => item.IsActive
                               && (item.VariantSku == normalizedChannelSku || item.BaseSku == normalizedChannelSku))
                .OrderBy(item => item.VariantSku)
                .ToListAsync(cancellationToken);
            var authorizedCandidates = new List<ProductVariant>();
            foreach (var candidate in skuCandidates)
            {
                var isAuthorized = await _catalogAuthorizationService.IsSkuAllowedAsync(
                    tenantId,
                    clientId,
                    candidate.BaseSku,
                    cancellationToken);
                if (isAuthorized) authorizedCandidates.Add(candidate);
            }

            if (authorizedCandidates.Count == 1)
            {
                var exactVariant = authorizedCandidates[0];
                var addedToMyProducts = await EnsureClientProductAsync(
                    tenantId,
                    clientId,
                    exactVariant.BaseSku,
                    Guid.Empty,
                    cancellationToken);
                var automaticMap = new TenantMarketplaceListingMap
                {
                    TenantId = tenantId,
                    ClientId = clientId,
                    Provider = provider,
                    IntegrationId = integrationId,
                    SellerId = sellerId,
                    MlItemId = normalizedItemId,
                    MlVariationId = normalizedVariationId,
                    ChannelSku = normalizedChannelSku,
                    SabrVariantSku = exactVariant.VariantSku,
                    MappingVersion = 1
                };
                _dbContext.TenantMarketplaceListingMaps.Add(automaticMap);
                _dbContext.AuditEvents.Add(new AuditEvent
                {
                    TenantId = tenantId,
                    ActorType = "System",
                    Action = "MarketplaceMapping.AutoMapExactSku",
                    Entity = nameof(TenantMarketplaceListingMap),
                    EntityId = automaticMap.Id,
                    RequestId = Guid.NewGuid(),
                    MetadataJson = JsonSerializer.Serialize(new
                    {
                        provider,
                        sellerId,
                        integrationId,
                        externalItemId = normalizedItemId,
                        externalVariationId = normalizedVariationId,
                        channelSku = normalizedChannelSku,
                        masterVariantSku = exactVariant.VariantSku,
                        automaticMap.MappingVersion,
                        addedToMyProducts
                    })
                });
                await _dbContext.SaveChangesAsync(cancellationToken);
                return new MarketplaceItemResolutionResult(
                    exactVariant.VariantSku,
                    MarketplaceMappingStates.MappedByExactSku,
                    MarketplaceMappingReasonCodes.MappedByExactSku,
                    normalizedChannelSku,
                    MarketplaceMappingReasonCodes.MappedByExactSku,
                    automaticMap.Id,
                    automaticMap.MappingVersion);
            }

            if (authorizedCandidates.Count > 1)
            {
                return new MarketplaceItemResolutionResult(
                    null,
                    MarketplaceMappingStates.UnmappedAmbiguousChannelSku,
                    MarketplaceMappingReasonCodes.UnmappedAmbiguousChannelSku,
                    normalizedChannelSku,
                    MarketplaceMappingReasonCodes.UnmappedAmbiguousChannelSku);
            }

            if (skuCandidates.Count > 0)
                return new MarketplaceItemResolutionResult(
                    null,
                    MarketplaceMappingStates.UnmappedSkuNotAuthorized,
                    MarketplaceMappingReasonCodes.UnmappedSkuNotAuthorized,
                    normalizedChannelSku,
                    MarketplaceMappingReasonCodes.UnmappedSkuNotAuthorized);

            return new MarketplaceItemResolutionResult(
                null,
                MarketplaceMappingStates.UnmappedUnknownChannelSku,
                MarketplaceMappingReasonCodes.UnmappedUnknownChannelSku,
                normalizedChannelSku,
                MarketplaceMappingReasonCodes.UnmappedUnknownChannelSku);
        }

        return new MarketplaceItemResolutionResult(
            null,
            MarketplaceMappingStates.UnmappedMissingChannelSku,
            MarketplaceMappingReasonCodes.UnmappedMissingChannelSku,
            null,
            MarketplaceMappingReasonCodes.UnmappedMissingChannelSku);
    }

    private async Task<bool> EnsureClientProductAsync(
        string tenantId,
        Guid clientId,
        string baseSku,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        var alreadyExists = await _dbContext.Publications.AsNoTracking().AnyAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.ProductSku == baseSku
                    && item.Status == PublicationStatus.Draft,
            cancellationToken);
        if (alreadyExists) return false;

        var product = await _dbContext.Products.AsNoTracking().FirstOrDefaultAsync(
            item => item.Sku == baseSku && item.IsActive,
            cancellationToken);
        if (product == null) return false;

        var now = DateTimeOffset.UtcNow;
        _dbContext.Publications.Add(new Publication
        {
            TenantId = tenantId,
            ClientId = clientId,
            ProductSku = product.Sku,
            Status = PublicationStatus.Draft,
            PricingMode = PricingMode.CatalogPrice,
            CostPriceCentsSnapshot = product.CostPriceCents,
            CatalogPriceCentsSnapshot = product.CatalogPriceCents,
            FinalPriceCentsSnapshot = product.CatalogPriceCents,
            PriceSnapshotTakenAt = now,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now
        });
        return true;
    }

    public static string? FindChannelSku(MarketplaceProvider provider, string? rawJson)
        => FindChannelMetadata(provider, rawJson).ChannelSku;

    public static void ApplyResolutionSnapshot(MarketplaceOrderItem item, MarketplaceItemResolutionResult resolution, DateTimeOffset resolvedAt)
    {
        item.SabrVariantSku = resolution.SabrVariantSku;
        item.MappingState = resolution.MappingState;
        item.MappingSnapshotId = resolution.MappingId;
        item.MappingSnapshotVersion = resolution.MappingVersion;
        item.MappingResolutionReason = resolution.MappingReason;
        item.MappingResolvedAt = resolvedAt;
    }

    public static string FindMappingReason(MarketplaceProvider provider, string? rawJson, string? mappingState, bool hasResolvedSku)
    {
        if (string.Equals(mappingState, MarketplaceMappingStates.Mapped, StringComparison.Ordinal))
        {
            return MarketplaceMappingReasonCodes.MappedByListingMap;
        }

        var metadata = FindChannelMetadata(provider, rawJson);
        return hasResolvedSku
            ? MarketplaceMappingReasonCodes.MappedByListingMap
            : string.IsNullOrWhiteSpace(metadata.ChannelSku)
                ? MarketplaceMappingReasonCodes.UnmappedMissingChannelSku
                : MarketplaceMappingReasonCodes.UnmappedUnknownChannelSku;
    }

    private async Task<ServiceResult<ProductVariant>> ResolveSelectedCatalogSkuAsync(
        string tenantId,
        Guid clientId,
        string selectedCatalogSku,
        CancellationToken cancellationToken)
    {
        var normalizedSku = NormalizeSku(selectedCatalogSku);
        if (normalizedSku == null)
        {
            return ServiceResult<ProductVariant>.Failure(
                ServiceErrorCodes.ValidationError,
                "selectedCatalogSku",
                "SKU do catalogo invalida.");
        }

        var exactVariant = await _dbContext.ProductVariants
            .FirstOrDefaultAsync(item => item.VariantSku == normalizedSku && item.IsActive, cancellationToken);
        if (exactVariant != null)
        {
            var exactVariantAllowed = await _catalogAuthorizationService.IsSkuAllowedAsync(
                tenantId,
                clientId,
                exactVariant.BaseSku,
                cancellationToken);
            if (!exactVariantAllowed)
            {
                return ServiceResult<ProductVariant>.Failure(
                    ServiceErrorCodes.SkuNotAuthorized,
                    "selectedCatalogSku",
                    "A SKU escolhida nao pertence ao catalogo liberado para este cliente.");
            }

            return ServiceResult<ProductVariant>.Success(exactVariant);
        }

        var product = await _dbContext.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Sku == normalizedSku && item.IsActive, cancellationToken);
        if (product == null)
        {
            return ServiceResult<ProductVariant>.Failure(
                ServiceErrorCodes.ValidationError,
                "selectedCatalogSku",
                "O produto ou variante selecionado nao existe.");
        }

        var isAllowed = await _catalogAuthorizationService.IsSkuAllowedAsync(
            tenantId,
            clientId,
            product.Sku,
            cancellationToken);
        if (!isAllowed)
        {
            return ServiceResult<ProductVariant>.Failure(
                ServiceErrorCodes.SkuNotAuthorized,
                "selectedCatalogSku",
                "A SKU escolhida nao pertence ao catalogo liberado para este cliente.");
        }

        var existingVariants = await _dbContext.ProductVariants
            .Where(item => item.BaseSku == product.Sku && item.IsActive)
            .OrderBy(item => item.VariantSku)
            .ToListAsync(cancellationToken);

        if (existingVariants.Count > 0)
        {
            return ServiceResult<ProductVariant>.Failure(
                ServiceErrorCodes.ValidationError,
                "selectedCatalogSku",
                "Este produto possui variantes ativas. Selecione a variante correta para o mapeamento.");
        }

        var createdVariant = new ProductVariant
        {
            VariantSku = product.Sku,
            BaseSku = product.Sku,
            Name = string.IsNullOrWhiteSpace(product.Name) ? product.Sku : product.Name.Trim(),
            CostPriceCents = Math.Max(0, product.CostPriceCents),
            CatalogPriceCents = Math.Max(0, product.CatalogPriceCents),
            PhysicalStock = 0,
            ReservedStock = 0,
            AvailableStock = 0,
            IsActive = product.IsActive,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.ProductVariants.Add(createdVariant);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<ProductVariant>.Success(createdVariant);
    }

    private async Task<ServiceResult<TenantMarketplaceConnection>> ResolveConnectionAsync(
        string tenantId,
        Guid clientId,
        MarketplaceProvider provider,
        long? sellerId,
        Guid? integrationId,
        CancellationToken cancellationToken)
    {
        var query = _dbContext.TenantMarketplaceConnections
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == provider);

        if (integrationId.HasValue)
        {
            query = query.Where(item => item.Id == integrationId.Value);
        }

        if (sellerId.HasValue)
        {
            query = query.Where(item => item.SellerId == sellerId.Value);
        }

        var connection = await query
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (connection == null)
        {
            return ServiceResult<TenantMarketplaceConnection>.Failure(
                ServiceErrorCodes.ValidationError,
                "connection",
                "Conexao do canal nao encontrada para este mapeamento.");
        }

        return ServiceResult<TenantMarketplaceConnection>.Success(connection);
    }

    private async Task<Dictionary<string, ProductVariant>> LoadVariantLookupAsync(
        IEnumerable<string> variantSkus,
        CancellationToken cancellationToken)
    {
        var skus = variantSkus
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (skus.Count == 0)
        {
            return new Dictionary<string, ProductVariant>(StringComparer.Ordinal);
        }

        return await _dbContext.ProductVariants
            .AsNoTracking()
            .Where(item => skus.Contains(item.VariantSku))
            .ToDictionaryAsync(item => item.VariantSku, StringComparer.Ordinal, cancellationToken);
    }

    private async Task<Dictionary<string, Product>> LoadProductLookupAsync(
        IEnumerable<string> baseSkus,
        CancellationToken cancellationToken)
    {
        var skus = baseSkus
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (skus.Count == 0)
        {
            return new Dictionary<string, Product>(StringComparer.Ordinal);
        }

        return await _dbContext.Products
            .AsNoTracking()
            .Where(item => skus.Contains(item.Sku))
            .ToDictionaryAsync(item => item.Sku, StringComparer.Ordinal, cancellationToken);
    }

    private static string ResolveMappingReason(MarketplaceOrderItem item, ChannelMetadata metadata)
    {
        if (MarketplaceMappingStates.IsMapped(item.MappingState))
        {
            return item.MappingState switch
            {
                MarketplaceMappingStates.MappedByExactSku => MarketplaceMappingReasonCodes.MappedByExactSku,
                _ => MarketplaceMappingReasonCodes.MappedByListingMap
            };
        }

        return item.MappingState switch
        {
            MarketplaceMappingStates.UnmappedSkuNotAuthorized => MarketplaceMappingReasonCodes.UnmappedSkuNotAuthorized,
            MarketplaceMappingStates.UnmappedMappingNotAuthorized => MarketplaceMappingReasonCodes.UnmappedMappedSkuNotAuthorized,
            MarketplaceMappingStates.UnmappedUnknownChannelSku => MarketplaceMappingReasonCodes.UnmappedUnknownChannelSku,
            MarketplaceMappingStates.UnmappedAmbiguousChannelSku => MarketplaceMappingReasonCodes.UnmappedAmbiguousChannelSku,
            MarketplaceMappingStates.UnmappedMissingChannelSku => MarketplaceMappingReasonCodes.UnmappedMissingChannelSku,
            _ => string.IsNullOrWhiteSpace(metadata.ChannelSku)
                ? MarketplaceMappingReasonCodes.UnmappedMissingChannelSku
                : MarketplaceMappingReasonCodes.UnmappedUnknownChannelSku
        };
    }

    private static ChannelMetadata FindChannelMetadata(MarketplaceOrderItem? item)
    {
        if (item == null) return new ChannelMetadata();
        var parsed = FindChannelMetadata(item.Provider, item.RawJson);
        return parsed with
        {
            ChannelSku = NormalizeNullable(item.ChannelSku) ?? parsed.ChannelSku,
            ProductName = NormalizeNullable(item.ProductName) ?? parsed.ProductName
        };
    }

    private static ChannelMetadata FindChannelMetadata(MarketplaceProvider provider, string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new ChannelMetadata();
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;

            return provider switch
            {
                MarketplaceProvider.TikTokShop => new ChannelMetadata(
                    ChannelSku: ReadString(root, "seller_sku") ?? ReadString(root, "SellerSku"),
                    ProductName: ReadString(root, "product_name") ?? ReadString(root, "ProductName"),
                    VariantName: ReadString(root, "sku_name") ?? ReadString(root, "SkuName")),
                MarketplaceProvider.Shopee => new ChannelMetadata(
                    ChannelSku: ReadString(root, "model_sku") ?? ReadString(root, "item_sku"),
                    ProductName: ReadString(root, "item_name"),
                    VariantName: ReadString(root, "model_name") ?? ReadString(root, "item_name")),
                MarketplaceProvider.Shopify => new ChannelMetadata(
                    ChannelSku: ReadString(root, "Sku") ?? ReadString(root, "sku"),
                    ProductName: ReadString(root, "Title") ?? ReadString(root, "title"),
                    VariantName: ReadString(root, "Title") ?? ReadString(root, "title")),
                MarketplaceProvider.TinyErp => new ChannelMetadata(
                    ChannelSku: ReadString(root, "Codigo") ?? ReadString(root, "codigo"),
                    ProductName: ReadString(root, "Descricao") ?? ReadString(root, "descricao"),
                    VariantName: ReadString(root, "Descricao") ?? ReadString(root, "descricao")),
                _ => ReadMercadoLivreMetadata(root)
            };
        }
        catch
        {
            return new ChannelMetadata();
        }
    }

    private static ChannelMetadata ReadMercadoLivreMetadata(JsonElement root)
    {
        var item = root.TryGetProperty("item", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested
            : root;
        return new ChannelMetadata(
            ChannelSku: ReadString(item, "seller_sku")
                        ?? ReadString(item, "seller_custom_field")
                        ?? ReadString(root, "seller_sku")
                        ?? ReadString(root, "seller_custom_field"),
            ProductName: ReadString(item, "title") ?? ReadString(item, "name")
                         ?? ReadString(root, "title") ?? ReadString(root, "name"),
            VariantName: ReadString(item, "variation_name") ?? ReadString(item, "sku")
                         ?? ReadString(root, "variation_name") ?? ReadString(root, "sku"),
            ThumbnailUrl: ReadString(item, "thumbnail") ?? ReadString(item, "thumbnail_url")
                          ?? ReadString(root, "thumbnail") ?? ReadString(root, "thumbnail_url"));
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? NormalizeNullable(property.GetString())
            : null;

    private static string BuildLookupKey(long sellerId, string itemId, string? variationId)
        => $"{sellerId}|{NormalizeKey(itemId)}|{NormalizeNullable(variationId) ?? string.Empty}";

    private static string BuildGroupKey(MarketplaceProvider provider, long sellerId, string itemId, string? variationId)
        => $"{(int)provider}|{sellerId}|{NormalizeKey(itemId)}|{NormalizeNullable(variationId) ?? string.Empty}";

    private static string NormalizeKey(string? value)
        => value?.Trim() ?? string.Empty;

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeSku(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : Sku.Normalize(value);

    private static long? NormalizeSellerId(MarketplaceProvider provider, string? sellerId)
    {
        if (string.IsNullOrWhiteSpace(sellerId))
        {
            return null;
        }

        return provider switch
        {
            MarketplaceProvider.MercadoLivre or MarketplaceProvider.TikTokShop or MarketplaceProvider.TinyErp or MarketplaceProvider.Shopee
                when long.TryParse(sellerId.Trim(), out var parsed) => parsed,
            MarketplaceProvider.Shopify => null,
            _ => null
        };
    }

    private sealed record ChannelMetadata(
        string? ChannelSku = null,
        string? ProductName = null,
        string? VariantName = null,
        string? ThumbnailUrl = null);
}
