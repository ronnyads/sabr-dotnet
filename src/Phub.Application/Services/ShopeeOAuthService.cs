using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Phub.Application.Abstractions;
using Phub.Application.Options;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class ShopeeOAuthService
{
    private readonly IAppDbContext _dbContext;
    private readonly IShopeeApiClient _apiClient;
    private readonly MarketplaceOrderNumberService _orderNumberService;
    private readonly MarketplaceOrderInventoryService _inventoryService;
    private readonly MarketplaceOrderMappingService _mappingService;
    private readonly ShopeeOptions _options;

    public ShopeeOAuthService(
        IAppDbContext dbContext,
        IShopeeApiClient apiClient,
        MarketplaceOrderNumberService orderNumberService,
        MarketplaceOrderInventoryService inventoryService,
        MarketplaceOrderMappingService mappingService,
        IOptions<ShopeeOptions> options)
    {
        _dbContext = dbContext;
        _apiClient = apiClient;
        _orderNumberService = orderNumberService;
        _inventoryService = inventoryService;
        _mappingService = mappingService;
        _options = options.Value;
    }

    public string BuildConnectUrl(string state)
    {
        var baseUri = new Uri(_options.AuthorizationBaseUrl.TrimEnd('/') + "/");
        var authorizeUri = new Uri(baseUri, _options.AuthorizationPath.TrimStart('/'));
        var query = new List<string>
        {
            $"partner_id={Uri.EscapeDataString(_options.PartnerId.Trim())}",
            $"auth_type={Uri.EscapeDataString(string.IsNullOrWhiteSpace(_options.AuthType) ? "seller" : _options.AuthType.Trim())}",
            $"redirect_uri={Uri.EscapeDataString(_options.RedirectUri)}",
            "response_type=code"
        };

        if (!string.IsNullOrWhiteSpace(state))
        {
            query.Add($"state={Uri.EscapeDataString(state)}");
        }

        return $"{authorizeUri}?{string.Join("&", query)}";
    }

    public bool IsAppConfigured(out string message)
    {
        if (LooksLikePlaceholder(_options.PartnerId) ||
            LooksLikePlaceholder(_options.PartnerKey) ||
            LooksLikePlaceholder(_options.RedirectUri))
        {
            message = "Shopee app is not configured. Configure PartnerId, PartnerKey and RedirectUri via user-secrets/env.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    public async Task<ServiceResult<TenantMarketplaceConnection>> HandleCallbackAsync(
        string tenantId,
        Guid clientId,
        string code,
        long? shopId,
        long? mainAccountId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return ServiceResult<TenantMarketplaceConnection>.Failure(new[]
            {
                new ValidationError("tenantId", "TenantId is required")
            });
        }

        if (clientId == Guid.Empty)
        {
            return ServiceResult<TenantMarketplaceConnection>.Failure(new[]
            {
                new ValidationError("clientId", "ClientId is required")
            });
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return ServiceResult<TenantMarketplaceConnection>.Failure(new[]
            {
                new ValidationError("code", "OAuth code is required")
            });
        }

        var clientExists = await _dbContext.Clients.AnyAsync(
            item => item.TenantId == tenantId && item.Id == clientId,
            cancellationToken);
        if (!clientExists)
        {
            return ServiceResult<TenantMarketplaceConnection>.Failure(new[]
            {
                new ValidationError("clientId", "Client not found in tenant")
            });
        }

        var token = await _apiClient.ExchangeCodeAsync(
            code.Trim(),
            shopId,
            mainAccountId,
            cancellationToken);
        EnsureTokenSucceeded(token, "exchange_code");

        var resolvedShopId = ResolveAuthorizedShopId(shopId, token);
        if (!resolvedShopId.HasValue || resolvedShopId.Value <= 0)
        {
            return ServiceResult<TenantMarketplaceConnection>.Failure(new[]
            {
                new ValidationError("shopId", "Shopee did not return an authorized shop_id.")
            });
        }

        var shopName = $"Shopee #{resolvedShopId.Value}";
        var shopInfo = await SafeGetShopInfoAsync(token.AccessToken, resolvedShopId.Value, cancellationToken);
        if (shopInfo?.Response != null)
        {
            shopName = BuildDisplayName(shopInfo.Response.ShopName, shopInfo.Response.Region, resolvedShopId.Value);
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var expiresAt = ResolveTokenExpiration(token.ExpireIn);

        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.Shopee,
            cancellationToken);

        if (connection == null)
        {
            connection = new TenantMarketplaceConnection
            {
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.Shopee,
                SellerId = resolvedShopId.Value,
                Nickname = shopName,
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken,
                TokenExpiresAt = expiresAt
            };
            _dbContext.TenantMarketplaceConnections.Add(connection);
        }
        else
        {
            connection.SellerId = resolvedShopId.Value;
            connection.Nickname = shopName;
            connection.AccessToken = token.AccessToken;
            connection.RefreshToken = token.RefreshToken;
            connection.TokenExpiresAt = expiresAt;
            connection.UpdatedAt = nowUtc;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<TenantMarketplaceConnection>.Success(connection);
    }

    public async Task<ServiceResult<ShopeeStatusResult>> GetClientStatusAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.Shopee,
            cancellationToken);

        if (connection == null)
        {
            return ServiceResult<ShopeeStatusResult>.Success(new ShopeeStatusResult
            {
                IsConnected = false
            });
        }

        var ordersCount = await _dbContext.MarketplaceOrders.CountAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.Shopee,
            cancellationToken);

        return ServiceResult<ShopeeStatusResult>.Success(new ShopeeStatusResult
        {
            IsConnected = true,
            ShopName = connection.Nickname,
            ConnectedAt = connection.CreatedAt,
            LastSyncAt = connection.LastSyncAt,
            TokenExpiresAt = connection.TokenExpiresAt,
            OrdersCount = ordersCount
        });
    }

    public async Task<ServiceResult<bool>> DisconnectAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.Shopee,
            cancellationToken);

        if (connection == null)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("shop", "Shopee connection not found")
            });
        }

        _dbContext.TenantMarketplaceConnections.Remove(connection);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<ShopeeSyncResult>> SyncNowAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Features.Sync)
        {
            return ServiceResult<ShopeeSyncResult>.Failure(
                ServiceErrorCodes.ValidationError,
                "sync",
                "Shopee sync is disabled.");
        }

        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.Shopee,
            cancellationToken);

        if (connection == null)
        {
            return ServiceResult<ShopeeSyncResult>.Failure(new[]
            {
                new ValidationError("shop", "Shopee connection not found")
            });
        }

        var accessToken = await GetValidAccessTokenAsync(connection, cancellationToken);
        var nowUtc = DateTimeOffset.UtcNow;
        var from = connection.LastSyncAt ?? nowUtc.AddDays(-Math.Max(1, _options.SyncLookbackDays));
        var oldestAllowedFrom = nowUtc.AddDays(-15);
        if (from < oldestAllowedFrom)
        {
            from = oldestAllowedFrom;
        }

        var collectedOrderSns = new List<string>();
        string? cursor = null;

        do
        {
            var listResponse = await _apiClient.GetOrderListAsync(
                accessToken,
                connection.SellerId,
                from.ToUnixTimeSeconds(),
                nowUtc.ToUnixTimeSeconds(),
                cursor,
                cancellationToken);
            EnsureEnvelopeSucceeded(listResponse, "get_order_list");

            var pageItems = listResponse.Response?.OrderList ?? [];
            foreach (var order in pageItems)
            {
                if (!string.IsNullOrWhiteSpace(order.OrderSn))
                {
                    collectedOrderSns.Add(order.OrderSn);
                }
            }

            cursor = listResponse.Response?.More == true
                ? listResponse.Response.NextCursor
                : null;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        var uniqueOrderSns = collectedOrderSns
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var batch in uniqueOrderSns.Chunk(50))
        {
            var detailResponse = await _apiClient.GetOrderDetailAsync(
                accessToken,
                connection.SellerId,
                batch,
                cancellationToken);
            EnsureEnvelopeSucceeded(detailResponse, "get_order_detail");

            foreach (var order in detailResponse.Response?.OrderList ?? [])
            {
                await UpsertOrderAsync(tenantId, clientId, connection, order, nowUtc, cancellationToken);
            }
        }

        connection.LastSyncAt = nowUtc;
        connection.UpdatedAt = nowUtc;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<ShopeeSyncResult>.Success(new ShopeeSyncResult
        {
            OrdersFetched = uniqueOrderSns.Count,
            SyncedAt = nowUtc
        });
    }

    public async Task SyncAllConnectionsAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Features.Sync)
        {
            return;
        }

        var connections = await _dbContext.TenantMarketplaceConnections
            .Where(item => item.Provider == MarketplaceProvider.Shopee
                           && item.AccessToken != null
                           && item.AccessToken != string.Empty)
            .ToListAsync(cancellationToken);

        foreach (var connection in connections)
        {
            await SyncNowAsync(connection.TenantId, connection.ClientId, cancellationToken);
        }
    }

    private async Task UpsertOrderAsync(
        string tenantId,
        Guid clientId,
        TenantMarketplaceConnection connection,
        ShopeeOrderDetail order,
        DateTimeOffset importedAt,
        CancellationToken cancellationToken)
    {
        var marketplaceOrder = await _dbContext.MarketplaceOrders
            .Include(item => item.Items)
            .FirstOrDefaultAsync(
                item => item.TenantId == tenantId
                        && item.Provider == MarketplaceProvider.Shopee
                        && item.MlOrderId == order.OrderSn,
                cancellationToken);

        if (marketplaceOrder == null)
        {
            marketplaceOrder = new MarketplaceOrder
            {
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.Shopee,
                SellerId = connection.SellerId,
                MlOrderId = order.OrderSn,
                ImportedAt = importedAt,
                CreatedAt = importedAt,
                UpdatedAt = importedAt
            };
            await _orderNumberService.EnsureOrderNumberAsync(marketplaceOrder, cancellationToken);
            _dbContext.MarketplaceOrders.Add(marketplaceOrder);
        }
        else if (string.IsNullOrWhiteSpace(marketplaceOrder.InternalOrderNumber))
        {
            await _orderNumberService.EnsureOrderNumberAsync(marketplaceOrder, cancellationToken);
        }

        var firstPackage = order.PackageList.FirstOrDefault();
        marketplaceOrder.SellerId = connection.SellerId;
        marketplaceOrder.Status = order.OrderStatus;
        marketplaceOrder.PaidAt = ToDateTimeOffset(order.PayTime);
        marketplaceOrder.ShipmentId = NormalizeNullable(firstPackage?.PackageNumber) ?? NormalizeNullable(order.BookingSn);
        marketplaceOrder.ShippingMode = NormalizeNullable(order.ShippingCarrier);
        marketplaceOrder.LogisticType = NormalizeNullable(firstPackage?.LogisticsStatus);
        marketplaceOrder.ShipByDeadlineAt = ToDateTimeOffset(order.ShipByDate);
        marketplaceOrder.RawJson = JsonSerializer.Serialize(order);
        marketplaceOrder.UpdatedAt = importedAt;

        foreach (var line in order.ItemList)
        {
            var itemId = line.ItemId.ToString();
            var variationId = line.ModelId.GetValueOrDefault() > 0
                ? line.ModelId.GetValueOrDefault().ToString()
                : null;
            var channelSku = NormalizeNullable(line.ModelSku) ?? NormalizeNullable(line.ItemSku);
            var resolution = await _mappingService.ResolveImportedItemAsync(
                tenantId,
                clientId,
                MarketplaceProvider.Shopee,
                connection.SellerId,
                connection.Id,
                itemId,
                variationId,
                channelSku,
                cancellationToken);

            var existingItem = marketplaceOrder.Items.FirstOrDefault(item =>
                item.MlItemId == itemId &&
                item.MlVariationId == variationId);

            if (existingItem == null)
            {
                existingItem = new MarketplaceOrderItem
                {
                    TenantId = tenantId,
                    ClientId = clientId,
                    Provider = MarketplaceProvider.Shopee,
                    SellerId = connection.SellerId,
                    MlItemId = itemId,
                    MlVariationId = variationId,
                    CreatedAt = importedAt
                };
                marketplaceOrder.Items.Add(existingItem);
            }

            existingItem.SellerId = connection.SellerId;
            existingItem.SabrVariantSku = resolution.SabrVariantSku;
            existingItem.Quantity = Math.Max(0, line.QuantityPurchased);
            existingItem.MappingState = resolution.MappingState;
            existingItem.RawJson = JsonSerializer.Serialize(line);
            existingItem.UpdatedAt = importedAt;
        }

        await _inventoryService.ReconcileReservationsAsync(
            marketplaceOrder,
            connection.SellerId,
            reservationTtlHours: 24,
            cancellationToken);
    }

    private async Task<string> GetValidAccessTokenAsync(
        TenantMarketplaceConnection connection,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connection.AccessToken))
        {
            throw new InvalidOperationException("SHOPEE_AUTH_INVALID");
        }

        var refreshThreshold = DateTimeOffset.UtcNow.AddMinutes(2);
        if (connection.TokenExpiresAt > refreshThreshold)
        {
            return connection.AccessToken;
        }

        if (string.IsNullOrWhiteSpace(connection.RefreshToken) || connection.SellerId <= 0)
        {
            throw new InvalidOperationException("SHOPEE_AUTH_INVALID");
        }

        var refreshed = await _apiClient.RefreshAccessTokenAsync(
            connection.RefreshToken,
            connection.SellerId,
            cancellationToken);
        EnsureRefreshSucceeded(refreshed, "refresh_access_token");

        connection.AccessToken = refreshed.AccessToken;
        connection.RefreshToken = string.IsNullOrWhiteSpace(refreshed.RefreshToken)
            ? connection.RefreshToken
            : refreshed.RefreshToken;
        connection.TokenExpiresAt = ResolveTokenExpiration(refreshed.ExpireIn);
        connection.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return connection.AccessToken;
    }

    private async Task<ShopeeApiEnvelope<ShopeeShopInfoResponse>?> SafeGetShopInfoAsync(
        string accessToken,
        long shopId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _apiClient.GetShopInfoAsync(accessToken, shopId, cancellationToken);
            return string.IsNullOrWhiteSpace(response.Error) ? response : null;
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureTokenSucceeded(ShopeeTokenResponse response, string operation)
    {
        if (!string.IsNullOrWhiteSpace(response.Error) || string.IsNullOrWhiteSpace(response.AccessToken))
        {
            throw new InvalidOperationException(
                $"Shopee {operation} failed: {response.Message ?? response.Error}".Trim());
        }
    }

    private static void EnsureRefreshSucceeded(ShopeeRefreshTokenResponse response, string operation)
    {
        if (!string.IsNullOrWhiteSpace(response.Error) || string.IsNullOrWhiteSpace(response.AccessToken))
        {
            throw new InvalidOperationException(
                $"Shopee {operation} failed: {response.Message ?? response.Error}".Trim());
        }
    }

    private static void EnsureEnvelopeSucceeded<T>(ShopeeApiEnvelope<T> response, string operation)
    {
        if (!string.IsNullOrWhiteSpace(response.Error))
        {
            throw new InvalidOperationException(
                $"Shopee {operation} failed: {response.Message ?? response.Error}".Trim());
        }
    }

    private static long? ResolveAuthorizedShopId(long? callbackShopId, ShopeeTokenResponse token)
    {
        if (callbackShopId.HasValue && callbackShopId.Value > 0)
        {
            return callbackShopId.Value;
        }

        if (token.ShopId.HasValue && token.ShopId.Value > 0)
        {
            return token.ShopId.Value;
        }

        return token.ShopIdList.FirstOrDefault(item => item > 0);
    }

    private static DateTimeOffset ResolveTokenExpiration(int expireInSeconds)
        => DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expireInSeconds));

    private static DateTimeOffset? ToDateTimeOffset(long? unixSeconds)
        => unixSeconds.HasValue && unixSeconds.Value > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value)
            : null;

    private static string BuildDisplayName(string? shopName, string? region, long shopId)
    {
        var normalizedName = NormalizeNullable(shopName);
        var normalizedRegion = NormalizeNullable(region);
        if (!string.IsNullOrWhiteSpace(normalizedName) && !string.IsNullOrWhiteSpace(normalizedRegion))
        {
            return $"{normalizedName} ({normalizedRegion})";
        }

        if (!string.IsNullOrWhiteSpace(normalizedName))
        {
            return normalizedName;
        }

        return $"Shopee #{shopId}";
    }

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool LooksLikePlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim();
        return normalized.StartsWith("__SET_VIA_", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("<", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(">", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ShopeeStatusResult
{
    public bool IsConnected { get; set; }
    public string? ShopName { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public int OrdersCount { get; set; }
}

public sealed class ShopeeSyncResult
{
    public int OrdersFetched { get; set; }
    public DateTimeOffset SyncedAt { get; set; }
}
