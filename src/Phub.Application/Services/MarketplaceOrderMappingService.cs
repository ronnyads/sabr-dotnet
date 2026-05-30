using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

    public MarketplaceOrderMappingService(
        IAppDbContext dbContext,
        CatalogAuthorizationService catalogAuthorizationService,
        MarketplaceOrderInventoryService inventoryService)
    {
        _dbContext = dbContext;
        _catalogAuthorizationService = catalogAuthorizationService;
        _inventoryService = inventoryService;
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
                SabrVariantSku = resolvedVariant.Data.VariantSku
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
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            action = "updated";
        }

        var matchedItems = await _dbContext.MarketplaceOrderItems
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == request.Provider
                           && item.SellerId == connection.Data.SellerId
                           && item.MlItemId == normalizedItemId
                           && item.MlVariationId == normalizedVariationId)
            .ToListAsync(cancellationToken);

        foreach (var item in matchedItems)
        {
            item.SabrVariantSku = resolvedVariant.Data.VariantSku;
            item.MappingState = MarketplaceMappingStates.MappedByListingMap;
            item.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var affectedOrderIds = matchedItems
            .Select(item => item.MarketplaceOrderId)
            .Distinct()
            .ToList();

        if (affectedOrderIds.Count > 0)
        {
            var orders = await _dbContext.MarketplaceOrders
                .Include(order => order.Items)
                .Where(order => affectedOrderIds.Contains(order.Id))
                .ToListAsync(cancellationToken);

            foreach (var order in orders)
            {
                await _inventoryService.ReconcileReservationsAsync(
                    order,
                    order.SellerId,
                    reservationTtlHours: 24,
                    cancellationToken);
                order.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var product = await _dbContext.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Sku == resolvedVariant.Data.BaseSku, cancellationToken);

        var firstMatchedItem = matchedItems.FirstOrDefault();
        var channelMetadata = FindChannelMetadata(firstMatchedItem);

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
            OrdersAffected = affectedOrderIds.Count,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = existing.UpdatedAt
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

        var matchedItems = await _dbContext.MarketplaceOrderItems
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == mapping.Provider
                           && item.SellerId == mapping.SellerId
                           && item.MlItemId == mapping.MlItemId
                           && item.MlVariationId == mapping.MlVariationId)
            .ToListAsync(cancellationToken);

        _dbContext.TenantMarketplaceListingMaps.Remove(mapping);

        foreach (var item in matchedItems)
        {
            var resolution = await ResolveImportedItemAsync(
                tenantId,
                clientId,
                item.Provider,
                item.SellerId,
                mapping.IntegrationId,
                item.MlItemId,
                item.MlVariationId,
                FindChannelSku(item.Provider, item.RawJson),
                cancellationToken,
                mapping.Id);

            item.SabrVariantSku = resolution.SabrVariantSku;
            item.MappingState = resolution.MappingState;
            item.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var affectedOrderIds = matchedItems
            .Select(item => item.MarketplaceOrderId)
            .Distinct()
            .ToList();

        if (affectedOrderIds.Count > 0)
        {
            var orders = await _dbContext.MarketplaceOrders
                .Include(order => order.Items)
                .Where(order => affectedOrderIds.Contains(order.Id))
                .ToListAsync(cancellationToken);

            foreach (var order in orders)
            {
                await _inventoryService.ReconcileReservationsAsync(
                    order,
                    order.SellerId,
                    reservationTtlHours: 24,
                    cancellationToken);
                order.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

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
            .FirstOrDefaultAsync(
                item => item.TenantId == tenantId
                        && item.ClientId == clientId
                        && item.Provider == provider
                        && (!ignoredMappingId.HasValue || item.Id != ignoredMappingId.Value)
                        && item.SellerId == sellerId
                        && item.MlItemId == normalizedItemId
                        && item.MlVariationId == normalizedVariationId
                        && (!integrationId.HasValue || item.IntegrationId == integrationId.Value),
                cancellationToken);

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
                        MarketplaceMappingReasonCodes.MappedByListingMap);
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
            var exactVariant = await _dbContext.ProductVariants
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.VariantSku == normalizedChannelSku && item.IsActive, cancellationToken);
            if (exactVariant != null)
            {
                var isAuthorized = await _catalogAuthorizationService.IsSkuAllowedAsync(
                    tenantId,
                    clientId,
                    exactVariant.BaseSku,
                    cancellationToken);
                if (isAuthorized)
                {
                    return new MarketplaceItemResolutionResult(
                        exactVariant.VariantSku,
                        MarketplaceMappingStates.MappedByExactSku,
                        MarketplaceMappingReasonCodes.MappedByExactSku,
                        normalizedChannelSku,
                        MarketplaceMappingReasonCodes.MappedByExactSku);
                }

                return new MarketplaceItemResolutionResult(
                    null,
                    MarketplaceMappingStates.UnmappedSkuNotAuthorized,
                    MarketplaceMappingReasonCodes.UnmappedSkuNotAuthorized,
                    normalizedChannelSku,
                    MarketplaceMappingReasonCodes.UnmappedSkuNotAuthorized);
            }

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

    public static string? FindChannelSku(MarketplaceProvider provider, string? rawJson)
        => FindChannelMetadata(provider, rawJson).ChannelSku;

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
            MarketplaceMappingStates.UnmappedMissingChannelSku => MarketplaceMappingReasonCodes.UnmappedMissingChannelSku,
            _ => string.IsNullOrWhiteSpace(metadata.ChannelSku)
                ? MarketplaceMappingReasonCodes.UnmappedMissingChannelSku
                : MarketplaceMappingReasonCodes.UnmappedUnknownChannelSku
        };
    }

    private static ChannelMetadata FindChannelMetadata(MarketplaceOrderItem? item)
        => item == null ? new ChannelMetadata() : FindChannelMetadata(item.Provider, item.RawJson);

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
                _ => new ChannelMetadata(
                    ChannelSku: ReadString(root, "seller_custom_field") ?? ReadString(root, "seller_sku"),
                    ProductName: ReadString(root, "title") ?? ReadString(root, "name"),
                    VariantName: ReadString(root, "variation_name") ?? ReadString(root, "sku"))
            };
        }
        catch
        {
            return new ChannelMetadata();
        }
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
        string? VariantName = null);
}
