using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sabr.Application.Abstractions;
using Sabr.Application.Validation;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;

namespace Sabr.Application.Services;

public sealed class TikTokShopOAuthService
{
    private readonly IAppDbContext _dbContext;
    private readonly ITikTokShopApiClient _apiClient;
    private readonly Options.TikTokShopOptions _options;

    public TikTokShopOAuthService(
        IAppDbContext dbContext,
        ITikTokShopApiClient apiClient,
        IOptions<Options.TikTokShopOptions> options)
    {
        _dbContext = dbContext;
        _apiClient = apiClient;
        _options = options.Value;
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

        var tokenData = tokenEnvelope.Data;
        var expiresAt = ResolveExpiration(tokenData.AccessTokenExpireIn, tokenData.ExpiresIn);
        var displayName = BuildDisplayName(tokenData);
        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.TikTokShop,
            cancellationToken);

        var sellerId = TryParseSellerId(tokenData.ShopId);
        if (connection == null)
        {
            connection = new TenantMarketplaceConnection
            {
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.TikTokShop,
                SellerId = sellerId,
                Nickname = displayName,
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

        return ServiceResult<TikTokShopStatusResult>.Success(new TikTokShopStatusResult
        {
            IsConnected = true,
            ShopName = connection.Nickname,
            ConnectedAt = connection.CreatedAt,
            LastSyncAt = connection.LastSyncAt,
            TokenExpiresAt = connection.TokenExpiresAt,
            OrdersCount = ordersCount,
            MappingsCount = mappingsCount
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
            return ServiceResult<string>.Failure(new[]
            {
                new ValidationError("connection", "TikTok Shop não está conectado")
            });
        }

        // Refresh se o token expira nos próximos 5 minutos
        var needsRefresh = connection.TokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(5);

        if (needsRefresh && !string.IsNullOrWhiteSpace(connection.RefreshToken))
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
                .Where(s => orderIds.Contains(s.MarketplaceOrderId))
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

    private static string BuildDisplayName(TikTokShopTokenPayload tokenData)
    {
        var sellerName = tokenData.SellerName?.Trim();
        var region = tokenData.SellerBaseRegion?.Trim().ToUpperInvariant();

        if (!string.IsNullOrWhiteSpace(sellerName) && !string.IsNullOrWhiteSpace(region))
        {
            return $"{sellerName} ({region})";
        }

        if (!string.IsNullOrWhiteSpace(sellerName))
        {
            return sellerName;
        }

        if (!string.IsNullOrWhiteSpace(tokenData.ShopCipher))
        {
            return tokenData.ShopCipher!;
        }

        return "TikTok Shop";
    }

    private static long TryParseSellerId(string? rawShopId)
    {
        return long.TryParse(rawShopId, out var parsed) ? parsed : 0L;
    }

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
}
