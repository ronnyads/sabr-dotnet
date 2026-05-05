using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Domain.ValueObjects;

namespace Phub.Application.Services;

public sealed class TikTokShopMappingService
{
    private readonly IAppDbContext _dbContext;
    private readonly CatalogAuthorizationService _catalogAuthorizationService;
    private readonly MarketplaceOrderInventoryService _inventoryService;

    public TikTokShopMappingService(
        IAppDbContext dbContext,
        CatalogAuthorizationService catalogAuthorizationService,
        MarketplaceOrderInventoryService inventoryService)
    {
        _dbContext = dbContext;
        _catalogAuthorizationService = catalogAuthorizationService;
        _inventoryService = inventoryService;
    }

    public async Task<ServiceResult<List<TikTokShopMappingResult>>> ListMappingsAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var maps = await _dbContext.TenantMarketplaceListingMaps
            .AsNoTracking()
            .Where(map => map.TenantId == tenantId
                          && map.ClientId == clientId
                          && map.Provider == MarketplaceProvider.TikTokShop)
            .OrderByDescending(map => map.CreatedAt)
            .ToListAsync(cancellationToken);
        var variantSkus = maps
            .Select(map => map.SabrVariantSku)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var variants = variantSkus.Count == 0
            ? new Dictionary<string, ProductVariant>(StringComparer.Ordinal)
            : await _dbContext.ProductVariants
                .AsNoTracking()
                .Where(item => variantSkus.Contains(item.VariantSku))
                .ToDictionaryAsync(item => item.VariantSku, StringComparer.Ordinal, cancellationToken);
        var baseSkus = variants.Values
            .Select(item => item.BaseSku)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var products = baseSkus.Count == 0
            ? new Dictionary<string, Product>(StringComparer.Ordinal)
            : await _dbContext.Products
                .AsNoTracking()
                .Where(item => baseSkus.Contains(item.Sku))
                .ToDictionaryAsync(item => item.Sku, StringComparer.Ordinal, cancellationToken);

        var result = maps.Select(map =>
        {
            variants.TryGetValue(map.SabrVariantSku, out var variant);
            var product = variant != null ? products.GetValueOrDefault(variant.BaseSku) : null;

            return new TikTokShopMappingResult
            {
                Id = map.Id,
                TikTokItemId = map.MlItemId,
                TikTokSkuId = map.MlVariationId,
                SabrVariantSku = map.SabrVariantSku,
                BaseSku = variant?.BaseSku,
                ProductName = product?.Name,
                VariantName = variant?.Name,
                CreatedAt = map.CreatedAt,
                UpdatedAt = map.UpdatedAt,
                Action = "loaded"
            };
        }).ToList();

        return ServiceResult<List<TikTokShopMappingResult>>.Success(result);
    }

    public async Task<ServiceResult<List<TikTokShopUnmappedItemResult>>> ListUnmappedItemsAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var items = await _dbContext.MarketplaceOrderItems
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == MarketplaceProvider.TikTokShop
                           && (item.SabrVariantSku == null || item.SabrVariantSku == string.Empty))
            .Join(
                _dbContext.MarketplaceOrders.AsNoTracking(),
                item => item.MarketplaceOrderId,
                order => order.Id,
                (item, order) => new { item, order })
            .OrderByDescending(entry => entry.order.ImportedAt)
            .ToListAsync(cancellationToken);

        var grouped = items
            .GroupBy(entry => BuildMappingKey(entry.item.MlItemId, entry.item.MlVariationId), StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var itemNames = ParseTikTokLineItemNames(first.item.RawJson);
                return new TikTokShopUnmappedItemResult
                {
                    MappingKey = group.Key,
                    TikTokItemId = NormalizeKey(first.item.MlItemId),
                    TikTokSkuId = NormalizeNullable(first.item.MlVariationId),
                    ProductName = itemNames.ProductName,
                    VariantName = itemNames.VariantName,
                    OrdersAffected = group.Select(entry => entry.order.Id).Distinct().Count(),
                    TotalUnits = group.Sum(entry => entry.item.Quantity),
                    LatestImportedAt = group.Max(entry => entry.order.ImportedAt)
                };
            })
            .OrderByDescending(item => item.LatestImportedAt)
            .ThenBy(item => item.TikTokItemId, StringComparer.Ordinal)
            .ToList();

        return ServiceResult<List<TikTokShopUnmappedItemResult>>.Success(grouped);
    }

    public async Task<ServiceResult<TikTokShopMappingResult>> CreateMappingAsync(
        string tenantId,
        Guid clientId,
        TikTokShopCreateMappingRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedItemId = NormalizeKey(request.TikTokItemId);
        var normalizedSkuId = NormalizeNullable(request.TikTokSkuId);
        var normalizedVariantSku = NormalizeVariantSku(request.SabrVariantSku);

        if (string.IsNullOrWhiteSpace(normalizedItemId))
        {
            return ServiceResult<TikTokShopMappingResult>.Failure(new[]
            {
                new ValidationError("tikTokItemId", "TikTok Item ID e obrigatorio")
            });
        }

        if (string.IsNullOrWhiteSpace(normalizedVariantSku))
        {
            return ServiceResult<TikTokShopMappingResult>.Failure(new[]
            {
                new ValidationError("sabrVariantSku", "SABR Variant SKU e obrigatorio")
            });
        }

        var variant = await _dbContext.ProductVariants
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.VariantSku == normalizedVariantSku, cancellationToken);
        if (variant == null || !variant.IsActive)
        {
            return ServiceResult<TikTokShopMappingResult>.Failure(
                ServiceErrorCodes.ValidationError,
                "sabrVariantSku",
                "A variante informada nao existe ou esta inativa.");
        }

        var isAuthorized = await _catalogAuthorizationService.IsSkuAllowedAsync(
            tenantId,
            clientId,
            variant.BaseSku,
            cancellationToken);
        if (!isAuthorized)
        {
            return ServiceResult<TikTokShopMappingResult>.Failure(
                ServiceErrorCodes.SkuNotAuthorized,
                "sabrVariantSku",
                "A variante escolhida nao pertence ao catalogo liberado para este cliente.");
        }

        var product = await _dbContext.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Sku == variant.BaseSku, cancellationToken);

        var connection = await _dbContext.TenantMarketplaceConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.TenantId == tenantId
                        && item.ClientId == clientId
                        && item.Provider == MarketplaceProvider.TikTokShop,
                cancellationToken);

        var mapping = await _dbContext.TenantMarketplaceListingMaps.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.TikTokShop
                    && item.MlItemId == normalizedItemId
                    && item.MlVariationId == normalizedSkuId,
            cancellationToken);

        var action = "created";
        if (mapping == null)
        {
            mapping = new TenantMarketplaceListingMap
            {
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.TikTokShop,
                SellerId = connection?.SellerId ?? 0,
                MlItemId = normalizedItemId,
                MlVariationId = normalizedSkuId,
                SabrVariantSku = normalizedVariantSku
            };
            _dbContext.TenantMarketplaceListingMaps.Add(mapping);
        }
        else if (string.Equals(mapping.SabrVariantSku, normalizedVariantSku, StringComparison.Ordinal))
        {
            action = "unchanged";
        }
        else
        {
            mapping.SellerId = connection?.SellerId ?? mapping.SellerId;
            mapping.SabrVariantSku = normalizedVariantSku;
            mapping.UpdatedAt = DateTimeOffset.UtcNow;
            action = "updated";
        }

        var matchedItems = await _dbContext.MarketplaceOrderItems
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == MarketplaceProvider.TikTokShop
                           && item.MlItemId == normalizedItemId
                           && item.MlVariationId == normalizedSkuId)
            .ToListAsync(cancellationToken);

        foreach (var item in matchedItems)
        {
            item.SabrVariantSku = normalizedVariantSku;
            item.MappingState = MarketplaceMappingStates.MappedByListingMap;
            item.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var affectedOrderIds = matchedItems
            .Select(item => item.MarketplaceOrderId)
            .Distinct()
            .ToList();
        if (affectedOrderIds.Count > 0)
        {
            var affectedOrders = await _dbContext.MarketplaceOrders
                .Include(order => order.Items)
                .Where(order => affectedOrderIds.Contains(order.Id))
                .ToListAsync(cancellationToken);

            foreach (var order in affectedOrders)
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

        return ServiceResult<TikTokShopMappingResult>.Success(new TikTokShopMappingResult
        {
            Id = mapping.Id,
            TikTokItemId = mapping.MlItemId,
            TikTokSkuId = mapping.MlVariationId,
            SabrVariantSku = mapping.SabrVariantSku,
            BaseSku = variant.BaseSku,
            ProductName = product?.Name,
            VariantName = variant.Name,
            CreatedAt = mapping.CreatedAt,
            UpdatedAt = mapping.UpdatedAt,
            Action = action,
            OrdersAffected = affectedOrderIds.Count
        });
    }

    public async Task<ServiceResult<bool>> DeleteMappingAsync(
        string tenantId,
        Guid clientId,
        Guid mappingId,
        CancellationToken cancellationToken = default)
    {
        var mapping = await _dbContext.TenantMarketplaceListingMaps.FirstOrDefaultAsync(
            m => m.Id == mappingId
                 && m.TenantId == tenantId
                 && m.ClientId == clientId
                 && m.Provider == MarketplaceProvider.TikTokShop,
            cancellationToken);

        if (mapping == null)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("mapping", "Mapeamento nao encontrado")
            });
        }

        _dbContext.TenantMarketplaceListingMaps.Remove(mapping);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<bool>.Success(true);
    }

    private static string BuildMappingKey(string tikTokItemId, string? tikTokSkuId)
        => $"{NormalizeKey(tikTokItemId)}|{NormalizeNullable(tikTokSkuId) ?? string.Empty}";

    private static string NormalizeKey(string value)
        => value?.Trim() ?? string.Empty;

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeVariantSku(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : Sku.Normalize(value);

    private static (string? ProductName, string? VariantName) ParseTikTokLineItemNames(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;

            string? productName = null;
            string? variantName = null;

            if (root.TryGetProperty("product_name", out var productProperty)
                && productProperty.ValueKind == JsonValueKind.String)
            {
                productName = productProperty.GetString();
            }

            if (root.TryGetProperty("sku_name", out var variantProperty)
                && variantProperty.ValueKind == JsonValueKind.String)
            {
                variantName = variantProperty.GetString();
            }

            return (NormalizeNullable(productName), NormalizeNullable(variantName));
        }
        catch
        {
            return (null, null);
        }
    }
}

public sealed class TikTokShopMappingResult
{
    public Guid Id { get; set; }
    public string TikTokItemId { get; set; } = string.Empty;
    public string? TikTokSkuId { get; set; }
    public string SabrVariantSku { get; set; } = string.Empty;
    public string? BaseSku { get; set; }
    public string? ProductName { get; set; }
    public string? VariantName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string Action { get; set; } = "created";
    public int OrdersAffected { get; set; }
}

public sealed class TikTokShopCreateMappingRequest
{
    public string TikTokItemId { get; set; } = string.Empty;
    public string? TikTokSkuId { get; set; }
    public string SabrVariantSku { get; set; } = string.Empty;
}

public sealed class TikTokShopUnmappedItemResult
{
    public string MappingKey { get; set; } = string.Empty;
    public string TikTokItemId { get; set; } = string.Empty;
    public string? TikTokSkuId { get; set; }
    public string? ProductName { get; set; }
    public string? VariantName { get; set; }
    public int OrdersAffected { get; set; }
    public int TotalUnits { get; set; }
    public DateTimeOffset LatestImportedAt { get; set; }
}
