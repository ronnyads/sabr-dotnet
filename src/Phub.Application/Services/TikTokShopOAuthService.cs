using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phub.Application.Abstractions;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class TikTokShopOAuthService
{
    private readonly IAppDbContext _dbContext;
    private readonly ITikTokShopApiClient _apiClient;
    private readonly Options.TikTokShopOptions _options;
    private readonly ILogger<TikTokShopOAuthService> _logger;

    public TikTokShopOAuthService(
        IAppDbContext dbContext,
        ITikTokShopApiClient apiClient,
        IOptions<Options.TikTokShopOptions> options,
        ILogger<TikTokShopOAuthService> logger)
    {
        _dbContext = dbContext;
        _apiClient = apiClient;
        _options = options.Value;
        _logger = logger;
    }

    public string BuildConnectUrl(string state)
    {
        var baseUri = new Uri(_options.AuthBaseUrl.TrimEnd('/') + "/");
        var authorizeUri = new Uri(baseUri, _options.AuthorizePath.TrimStart('/'));
        var query = new List<string>
        {
            $"app_key={Uri.EscapeDataString(_options.AppKey)}",
            $"state={Uri.EscapeDataString(state)}",
            $"redirect_uri={Uri.EscapeDataString(_options.RedirectUri)}",
            "response_type=code"
        };

        if (!string.IsNullOrWhiteSpace(_options.Scopes))
        {
            query.Add($"scope={Uri.EscapeDataString(_options.Scopes)}");
        }

        return $"{authorizeUri}?{string.Join("&", query)}";
    }

    public bool IsAppConfigured(out string message)
    {
        if (LooksLikePlaceholder(_options.AppKey) ||
            LooksLikePlaceholder(_options.AppSecret) ||
            LooksLikePlaceholder(_options.RedirectUri))
        {
            message = "TikTok Shop app is not configured. Configure AppKey, AppSecret and RedirectUri via user-secrets/env.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    public async Task<ServiceResult<TenantMarketplaceConnection>> HandleCallbackAsync(
        string tenantId,
        Guid clientId,
        string code,
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

        var tokenEnvelope = await _apiClient.ExchangeCodeAsync(
            _options.AppKey,
            _options.AppSecret,
            code.Trim(),
            cancellationToken);

        if (tokenEnvelope.Code != 0 || tokenEnvelope.Data == null || string.IsNullOrWhiteSpace(tokenEnvelope.Data.AccessToken))
        {
            return ServiceResult<TenantMarketplaceConnection>.Failure(new[]
            {
                new ValidationError("accessToken", string.IsNullOrWhiteSpace(tokenEnvelope.Message)
                    ? "Access token not returned by TikTok Shop"
                    : tokenEnvelope.Message)
            });
        }

        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.TikTokShop,
            cancellationToken);
        var tokenData = tokenEnvelope.Data;
        var resolvedShop = await ResolveAuthorizedShopAsync(tokenData, connection, cancellationToken);
        var expiresAt = ResolveExpiration(tokenData.AccessTokenExpireIn, tokenData.ExpiresIn);
        var displayName = BuildDisplayName(
            resolvedShop.DisplayName,
            resolvedShop.Region,
            resolvedShop.ShopCipher);

        var sellerId = TryParseSellerId(resolvedShop.ShopId);
        if (!resolvedShop.IsComplete)
        {
            _logger.LogWarning(
                "TikTok OAuth callback persisted incomplete shop metadata. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} hasShopCipher={HasShopCipher} usedExistingMetadata={UsedExistingMetadata} operation={Operation}",
                tenantId,
                clientId,
                sellerId,
                !string.IsNullOrWhiteSpace(resolvedShop.ShopCipher),
                resolvedShop.UsedExistingMetadata,
                "oauth_callback");
        }

        if (connection == null)
        {
            connection = new TenantMarketplaceConnection
            {
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.TikTokShop,
                SellerId = sellerId,
                Nickname = displayName,
                ShopCipher = NormalizeNullable(resolvedShop.ShopCipher),
                AccessToken = tokenData.AccessToken,
                RefreshToken = tokenData.RefreshToken ?? string.Empty,
                TokenExpiresAt = expiresAt
            };
            _dbContext.TenantMarketplaceConnections.Add(connection);
        }
        else
        {
            connection.SellerId = sellerId;
            connection.Nickname = displayName;
            connection.ShopCipher = NormalizeNullable(resolvedShop.ShopCipher);
            connection.AccessToken = tokenData.AccessToken;
            connection.RefreshToken = tokenData.RefreshToken ?? connection.RefreshToken;
            connection.TokenExpiresAt = expiresAt;
            connection.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<TenantMarketplaceConnection>.Success(connection);
    }

    public async Task<ServiceResult<TikTokShopStatusResult>> GetClientStatusAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.TikTokShop,
            cancellationToken);

        if (connection == null)
        {
            return ServiceResult<TikTokShopStatusResult>.Success(new TikTokShopStatusResult
            {
                IsConnected = false
            });
        }

        var ordersCount = await _dbContext.MarketplaceOrders.CountAsync(
            o => o.TenantId == tenantId && o.ClientId == clientId && o.Provider == MarketplaceProvider.TikTokShop,
            cancellationToken);

        var mappingsCount = await _dbContext.TenantMarketplaceListingMaps.CountAsync(
            m => m.TenantId == tenantId && m.ClientId == clientId && m.Provider == MarketplaceProvider.TikTokShop,
            cancellationToken);
        var ordersMissingItemsCount = await _dbContext.MarketplaceOrders.CountAsync(
            o => o.TenantId == tenantId
                 && o.ClientId == clientId
                 && o.Provider == MarketplaceProvider.TikTokShop
                 && !o.Items.Any(),
            cancellationToken);
        var latestSyncLog = await _dbContext.MarketplaceEventLogs
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId
                        && e.ClientId == clientId
                        && e.Provider == MarketplaceProvider.TikTokShop
                        && e.Topic == "sync_orders")
            .OrderByDescending(e => e.UpdatedAt)
            .ThenByDescending(e => e.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var requiresReconnect = string.IsNullOrWhiteSpace(connection.ShopCipher) || connection.SellerId <= 0;
        var syncSnapshot = ReadSyncStatusSnapshot(latestSyncLog);

        var syncHealth = requiresReconnect
            ? "blocked"
            : ordersMissingItemsCount > 0
                ? "degraded"
                : syncSnapshot?.SyncHealth ?? "healthy";
        var syncBlockingReason = requiresReconnect
            ? "connection_reconnect_required"
            : ordersMissingItemsCount > 0
                ? "no_imported_items"
                : syncSnapshot?.BlockingReason;
        var lastSyncErrorCode = syncSnapshot?.ErrorCode;
        var lastSyncErrorMessage = ordersMissingItemsCount > 0
            ? $"Existem {ordersMissingItemsCount} pedido(s) TikTok sem itens importados para mapear."
            : syncSnapshot?.Message;

        return ServiceResult<TikTokShopStatusResult>.Success(new TikTokShopStatusResult
        {
            IsConnected = true,
            ShopName = connection.Nickname,
            ConnectedAt = connection.CreatedAt,
            LastSyncAt = connection.LastSyncAt,
            TokenExpiresAt = connection.TokenExpiresAt,
            OrdersCount = ordersCount,
            MappingsCount = mappingsCount,
            RequiresReconnect = requiresReconnect,
            SyncHealth = syncHealth,
            SyncBlockingReason = syncBlockingReason,
            LastSyncErrorCode = lastSyncErrorCode,
            LastSyncErrorMessage = lastSyncErrorMessage,
            OrdersMissingItemsCount = ordersMissingItemsCount,
            ConnectionWarning = requiresReconnect
                ? "A autorizacao foi concluida, mas o TikTok Shop nao retornou os dados completos da loja. Reconecte a conta para liberar categorias e publicacao."
                : null
        });
    }

    public async Task<ServiceResult<string>> GetValidAccessTokenAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.TikTokShop,
            cancellationToken);

        if (connection == null)
        {
            return ServiceResult<string>.Failure(
                ServiceErrorCodes.TikTokShopNotConnected,
                new[]
                {
                    new ValidationError("connection", "TikTok Shop nao esta conectado")
                });
        }

        var needsRefresh = connection.TokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(5);

        if (needsRefresh && !string.IsNullOrWhiteSpace(connection.RefreshToken))
        {
            try
            {
                var refreshEnvelope = await _apiClient.RefreshTokenAsync(
                    _options.AppKey,
                    _options.AppSecret,
                    connection.RefreshToken,
                    cancellationToken);

                if (refreshEnvelope.Code == 0 && refreshEnvelope.Data != null &&
                    !string.IsNullOrWhiteSpace(refreshEnvelope.Data.AccessToken))
                {
                    var tokenData = refreshEnvelope.Data;
                    connection.AccessToken = tokenData.AccessToken;
                    connection.RefreshToken = tokenData.RefreshToken ?? connection.RefreshToken;
                    connection.TokenExpiresAt = ResolveExpiration(tokenData.AccessTokenExpireIn, tokenData.ExpiresIn);
                    connection.UpdatedAt = DateTimeOffset.UtcNow;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
            }
            catch (TikTokShopApiException ex) when (IsAuthRefreshFailure(ex.StatusCode))
            {
                _logger.LogWarning(
                    ex,
                    "TikTok refresh token rejected. tenantId={TenantId} clientId={ClientId} statusCode={StatusCode} requestId={RequestId} operation={Operation}",
                    tenantId,
                    clientId,
                    ex.StatusCode,
                    ex.RequestId,
                    "refresh_token");
                return ServiceResult<string>.Failure(
                    ServiceErrorCodes.TikTokShopReconnectRequired,
                    "connection",
                    "Sessao TikTok Shop expirada. Reconecte sua conta.");
            }
            catch (TikTokShopApiException ex)
            {
                _logger.LogError(
                    ex,
                    "TikTok refresh token failed upstream. tenantId={TenantId} clientId={ClientId} statusCode={StatusCode} requestId={RequestId} operation={Operation}",
                    tenantId,
                    clientId,
                    ex.StatusCode,
                    ex.RequestId,
                    "refresh_token");
                return ServiceResult<string>.Failure(
                    ServiceErrorCodes.TikTokShopUpstreamError,
                    "connection",
                    "TikTok Shop indisponivel no momento. Tente novamente.");
            }
            catch (HttpRequestException ex) when (IsAuthRefreshFailure(ex.StatusCode))
            {
                _logger.LogWarning(
                    ex,
                    "TikTok refresh token rejected via HTTP exception. tenantId={TenantId} clientId={ClientId} statusCode={StatusCode} operation={Operation}",
                    tenantId,
                    clientId,
                    ex.StatusCode,
                    "refresh_token");
                return ServiceResult<string>.Failure(
                    ServiceErrorCodes.TikTokShopReconnectRequired,
                    "connection",
                    "Sessao TikTok Shop expirada. Reconecte sua conta.");
            }
        }

        return ServiceResult<string>.Success(connection.AccessToken);
    }

    public async Task<ServiceResult<bool>> DisconnectAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.TikTokShop,
            cancellationToken);

        if (connection == null)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("connection", "TikTok Shop connection not found")
            });
        }

        _dbContext.TenantMarketplaceConnections.Remove(connection);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<bool>> ResetAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("context", "Invalid tenant/client context")
            });
        }

        var orderIds = await _dbContext.MarketplaceOrders
            .Where(o => o.TenantId == tenantId
                        && o.ClientId == clientId
                        && o.Provider == MarketplaceProvider.TikTokShop)
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);

        if (orderIds.Count > 0)
        {
            var reservations = await _dbContext.StockReservations
                .Where(r => orderIds.Contains(r.MarketplaceOrderId))
                .ToListAsync(cancellationToken);
            _dbContext.StockReservations.RemoveRange(reservations);

            var shipments = await _dbContext.MarketplaceShipments
                .Where(s => s.TenantId == tenantId
                            && s.ClientId == clientId
                            && s.Provider == MarketplaceProvider.TikTokShop)
                .ToListAsync(cancellationToken);
            _dbContext.MarketplaceShipments.RemoveRange(shipments);

            var items = await _dbContext.MarketplaceOrderItems
                .Where(i => orderIds.Contains(i.MarketplaceOrderId))
                .ToListAsync(cancellationToken);
            _dbContext.MarketplaceOrderItems.RemoveRange(items);

            var orders = await _dbContext.MarketplaceOrders
                .Where(o => orderIds.Contains(o.Id))
                .ToListAsync(cancellationToken);
            _dbContext.MarketplaceOrders.RemoveRange(orders);
        }

        var eventLogs = await _dbContext.MarketplaceEventLogs
            .Where(e => e.TenantId == tenantId
                        && e.ClientId == clientId
                        && e.Provider == MarketplaceProvider.TikTokShop)
            .ToListAsync(cancellationToken);
        _dbContext.MarketplaceEventLogs.RemoveRange(eventLogs);

        var slaRules = await _dbContext.TenantMarketplaceSlaRules
            .Where(r => r.TenantId == tenantId
                        && r.ClientId == clientId
                        && r.Provider == MarketplaceProvider.TikTokShop)
            .ToListAsync(cancellationToken);
        _dbContext.TenantMarketplaceSlaRules.RemoveRange(slaRules);

        var listingMaps = await _dbContext.TenantMarketplaceListingMaps
            .Where(m => m.TenantId == tenantId
                        && m.ClientId == clientId
                        && m.Provider == MarketplaceProvider.TikTokShop)
            .ToListAsync(cancellationToken);
        _dbContext.TenantMarketplaceListingMaps.RemoveRange(listingMaps);

        var connections = await _dbContext.TenantMarketplaceConnections
            .Where(c => c.TenantId == tenantId
                        && c.ClientId == clientId
                        && c.Provider == MarketplaceProvider.TikTokShop)
            .ToListAsync(cancellationToken);
        _dbContext.TenantMarketplaceConnections.RemoveRange(connections);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<bool>.Success(true);
    }

    private async Task<ResolvedTikTokShopShop> ResolveAuthorizedShopAsync(
        TikTokShopTokenPayload tokenData,
        TenantMarketplaceConnection? existingConnection,
        CancellationToken cancellationToken)
    {
        if (TryCreateResolvedShop(
                tokenData.ShopId,
                tokenData.ShopCipher,
                tokenData.SellerName,
                tokenData.SellerBaseRegion,
                out var resolvedFromToken))
        {
            return resolvedFromToken;
        }

        try
        {
            var shops = await _apiClient.GetAuthorizedShopsAsync(
                tokenData.AccessToken,
                _options.AppKey,
                _options.AppSecret,
                cancellationToken);

            var matchingShop = shops
                .Where(shop => IsUsableShop(shop.ShopId, shop.ShopCipher))
                .OrderByDescending(shop => string.Equals(shop.ShopId, tokenData.ShopId, StringComparison.Ordinal))
                .ThenByDescending(shop => string.Equals(shop.ShopCipher, tokenData.ShopCipher, StringComparison.Ordinal))
                .FirstOrDefault();

            if (matchingShop is not null &&
                TryCreateResolvedShop(
                    matchingShop.ShopId,
                    matchingShop.ShopCipher,
                    matchingShop.SellerName ?? matchingShop.ShopName ?? tokenData.SellerName,
                    matchingShop.SellerBaseRegion ?? tokenData.SellerBaseRegion,
                    out var resolvedFromApi))
            {
                return resolvedFromApi;
            }
        }
        catch (TikTokShopApiException ex)
        {
            _logger.LogWarning(
                ex,
                "TikTok authorized shops lookup failed during OAuth callback. statusCode={StatusCode} requestId={RequestId} hasTokenShopCipher={HasTokenShopCipher} tokenShopId={TokenShopId} operation={Operation}",
                ex.StatusCode,
                ex.RequestId,
                !string.IsNullOrWhiteSpace(tokenData.ShopCipher),
                tokenData.ShopId,
                ex.Operation);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "TikTok authorized shops lookup failed with HTTP exception during OAuth callback. statusCode={StatusCode} hasTokenShopCipher={HasTokenShopCipher} tokenShopId={TokenShopId} operation={Operation}",
                ex.StatusCode,
                !string.IsNullOrWhiteSpace(tokenData.ShopCipher),
                tokenData.ShopId,
                "authorized_shops");
        }

        if (existingConnection != null &&
            TryCreateResolvedShop(
                existingConnection.SellerId > 0 ? existingConnection.SellerId.ToString() : null,
                existingConnection.ShopCipher,
                existingConnection.Nickname,
                tokenData.SellerBaseRegion,
                out var resolvedFromExisting))
        {
            _logger.LogWarning(
                "TikTok OAuth callback reused existing shop metadata after incomplete authorization payload. sellerId={SellerId} hasShopCipher={HasShopCipher} operation={Operation}",
                existingConnection.SellerId,
                !string.IsNullOrWhiteSpace(existingConnection.ShopCipher),
                "oauth_callback");
            return resolvedFromExisting with { UsedExistingMetadata = true };
        }

        _logger.LogWarning(
            "TikTok OAuth callback continuing with incomplete shop metadata. hasTokenShopCipher={HasTokenShopCipher} tokenShopId={TokenShopId} operation={Operation}",
            !string.IsNullOrWhiteSpace(tokenData.ShopCipher),
            tokenData.ShopId,
            "oauth_callback");
        return ResolvedTikTokShopShop.CreateIncomplete(
            tokenData.ShopId,
            tokenData.ShopCipher,
            tokenData.SellerName,
            tokenData.SellerBaseRegion);
    }

    private static bool TryCreateResolvedShop(
        string? shopId,
        string? shopCipher,
        string? displayName,
        string? region,
        out ResolvedTikTokShopShop resolvedShop)
    {
        if (!IsUsableShop(shopId, shopCipher))
        {
            resolvedShop = default!;
            return false;
        }

        resolvedShop = new ResolvedTikTokShopShop(
            shopId!.Trim(),
            shopCipher!.Trim(),
            displayName?.Trim(),
            region?.Trim(),
            true);
        return true;
    }

    private static bool IsUsableShop(string? shopId, string? shopCipher)
        => TryParseSellerId(shopId) > 0 && !string.IsNullOrWhiteSpace(shopCipher);

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string BuildDisplayName(string? sellerName, string? region, string? shopCipher)
    {
        sellerName = sellerName?.Trim();
        region = region?.Trim().ToUpperInvariant();

        if (!string.IsNullOrWhiteSpace(sellerName) && !string.IsNullOrWhiteSpace(region))
        {
            return $"{sellerName} ({region})";
        }

        if (!string.IsNullOrWhiteSpace(sellerName))
        {
            return sellerName;
        }

        if (!string.IsNullOrWhiteSpace(shopCipher))
        {
            return shopCipher!;
        }

        return "TikTok Shop";
    }

    private static long TryParseSellerId(string? rawShopId)
    {
        return long.TryParse(rawShopId, out var parsed) ? parsed : 0L;
    }

    private static bool IsAuthRefreshFailure(HttpStatusCode? statusCode)
        => statusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static DateTimeOffset ResolveExpiration(long? absoluteEpochSeconds, long? fallbackSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        if (absoluteEpochSeconds.HasValue && absoluteEpochSeconds.Value > now.ToUnixTimeSeconds())
        {
            return DateTimeOffset.FromUnixTimeSeconds(absoluteEpochSeconds.Value);
        }

        if (fallbackSeconds.HasValue && fallbackSeconds.Value > 0)
        {
            return now.AddSeconds(fallbackSeconds.Value);
        }

        return now.AddHours(4);
    }

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

    private static TikTokShopSyncStatusSnapshot? ReadSyncStatusSnapshot(MarketplaceEventLog? log)
    {
        if (log == null || string.IsNullOrWhiteSpace(log.PayloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(log.PayloadJson);
            var root = document.RootElement;
            return new TikTokShopSyncStatusSnapshot(
                ReadString(root, "syncHealth"),
                ReadString(root, "errorCode"),
                ReadString(root, "blockingReason"),
                ReadString(root, "message"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    private sealed record ResolvedTikTokShopShop(
        string? ShopId,
        string? ShopCipher,
        string? DisplayName,
        string? Region,
        bool IsComplete,
        bool UsedExistingMetadata = false)
    {
        public static ResolvedTikTokShopShop CreateIncomplete(
            string? shopId,
            string? shopCipher,
            string? displayName,
            string? region)
            => new(
                NormalizeNullable(shopId),
                NormalizeNullable(shopCipher),
                NormalizeNullable(displayName),
                NormalizeNullable(region),
                false);
    }

    private sealed record TikTokShopSyncStatusSnapshot(
        string? SyncHealth,
        string? ErrorCode,
        string? BlockingReason,
        string? Message);
}

public sealed class TikTokShopStatusResult
{
    public bool IsConnected { get; set; }
    public string? ShopName { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public int OrdersCount { get; set; }
    public int MappingsCount { get; set; }
    public bool RequiresReconnect { get; set; }
    public string? SyncHealth { get; set; }
    public string? SyncBlockingReason { get; set; }
    public string? LastSyncErrorCode { get; set; }
    public string? LastSyncErrorMessage { get; set; }
    public int OrdersMissingItemsCount { get; set; }
    public string? ConnectionWarning { get; set; }
}
