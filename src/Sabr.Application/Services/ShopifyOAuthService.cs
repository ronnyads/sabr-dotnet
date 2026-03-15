using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Options;
using Sabr.Application.Validation;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;

namespace Sabr.Application.Services;

public sealed class ShopifyOAuthService
{
    private readonly IAppDbContext _dbContext;
    private readonly IShopifyApiClient _shopifyApiClient;
    private readonly ShopifyOptions _options;

    public ShopifyOAuthService(
        IAppDbContext dbContext,
        IShopifyApiClient shopifyApiClient,
        IOptions<ShopifyOptions> options)
    {
        _dbContext = dbContext;
        _shopifyApiClient = shopifyApiClient;
        _options = options.Value;
    }

    public string BuildConnectUrl(string state, string shop)
    {
        var normalizedShop = shop.Trim().ToLowerInvariant();
        return $"https://{normalizedShop}/admin/oauth/authorize" +
               $"?client_id={Uri.EscapeDataString(_options.ClientId)}" +
               $"&scope={Uri.EscapeDataString(_options.Scopes)}" +
               $"&redirect_uri={Uri.EscapeDataString(_options.RedirectUri)}" +
               $"&state={Uri.EscapeDataString(state)}";
    }

    public bool IsAppConfigured(out string message)
    {
        if (LooksLikePlaceholder(_options.ClientId) ||
            LooksLikePlaceholder(_options.ClientSecret) ||
            LooksLikePlaceholder(_options.RedirectUri))
        {
            message = "Shopify app is not configured. Configure ClientId, ClientSecret and RedirectUri via user-secrets/env.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    public async Task<ServiceResult<TenantMarketplaceConnection>> HandleCallbackAsync(
        string tenantId,
        Guid clientId,
        string shop,
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

        if (string.IsNullOrWhiteSpace(shop))
        {
            return ServiceResult<TenantMarketplaceConnection>.Failure(new[]
            {
                new ValidationError("shop", "Shop domain is required")
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

        var normalizedShop = shop.Trim().ToLowerInvariant();

        var token = await _shopifyApiClient.ExchangeCodeAsync(normalizedShop, code, cancellationToken);
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            return ServiceResult<TenantMarketplaceConnection>.Failure(new[]
            {
                new ValidationError("accessToken", "Access token not returned by Shopify")
            });
        }

        var shopInfo = await _shopifyApiClient.GetShopInfoAsync(normalizedShop, token.AccessToken, cancellationToken);

        var nowUtc = DateTimeOffset.UtcNow;

        // Shopify offline access tokens do not expire — set a far-future ExpiresAt
        var expiresAt = nowUtc.AddYears(10);

        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.Shopify
                    && item.Nickname == normalizedShop,
            cancellationToken);

        if (connection == null)
        {
            connection = new TenantMarketplaceConnection
            {
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.Shopify,
                SellerId = 0,                          // Not used for Shopify
                Nickname = normalizedShop,              // shop domain stored in Nickname
                AccessToken = token.AccessToken,
                RefreshToken = string.Empty,            // Shopify offline tokens have no refresh
                TokenExpiresAt = expiresAt
            };
            _dbContext.TenantMarketplaceConnections.Add(connection);
        }
        else
        {
            connection.Nickname = normalizedShop;
            connection.AccessToken = token.AccessToken;
            connection.TokenExpiresAt = expiresAt;
            connection.UpdatedAt = nowUtc;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<TenantMarketplaceConnection>.Success(connection);
    }

    public Task<string> GetValidAccessTokenAsync(
        TenantMarketplaceConnection connection,
        CancellationToken cancellationToken = default)
    {
        // Shopify offline tokens do not expire — return directly
        if (string.IsNullOrWhiteSpace(connection.AccessToken))
        {
            throw new InvalidOperationException("SHOPIFY_AUTH_INVALID");
        }

        return Task.FromResult(connection.AccessToken);
    }

    public async Task<ServiceResult<ShopifyStatusResult>> GetClientStatusAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.Shopify,
            cancellationToken);

        if (connection == null)
        {
            return ServiceResult<ShopifyStatusResult>.Success(new ShopifyStatusResult
            {
                IsConnected = false
            });
        }

        return ServiceResult<ShopifyStatusResult>.Success(new ShopifyStatusResult
        {
            IsConnected = true,
            Shop = connection.Nickname,
            ConnectedAt = connection.CreatedAt,
            LastSyncAt = connection.LastSyncAt
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
                    && item.Provider == MarketplaceProvider.Shopify,
            cancellationToken);

        if (connection == null)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("shop", "Shopify connection not found")
            });
        }

        _dbContext.TenantMarketplaceConnections.Remove(connection);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<ShopifySyncResult>> SyncNowAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.Shopify,
            cancellationToken);

        if (connection == null)
        {
            return ServiceResult<ShopifySyncResult>.Failure(new[]
            {
                new ValidationError("shop", "Shopify connection not found")
            });
        }

        var accessToken = await GetValidAccessTokenAsync(connection, cancellationToken);
        var shop = connection.Nickname!;

        var orders = await _shopifyApiClient.GetOrdersAsync(
            shop,
            accessToken,
            since: connection.LastSyncAt,
            cancellationToken: cancellationToken);

        var nowUtc = DateTimeOffset.UtcNow;

        foreach (var order in orders)
        {
            var orderId = order.Id.ToString();
            var existing = await _dbContext.MarketplaceOrders
                .Include(o => o.Items)
                .FirstOrDefaultAsync(
                    o => o.TenantId == tenantId
                         && o.Provider == MarketplaceProvider.Shopify
                         && o.MlOrderId == orderId,
                    cancellationToken);

            if (existing == null)
            {
                var marketplaceOrder = new MarketplaceOrder
                {
                    TenantId = tenantId,
                    ClientId = clientId,
                    Provider = MarketplaceProvider.Shopify,
                    MlOrderId = orderId,
                    Status = order.FinancialStatus,
                    PaidAt = order.FinancialStatus == "paid" ? order.CreatedAt : null,
                    ImportedAt = nowUtc,
                    RawJson = JsonSerializer.Serialize(order),
                    CreatedAt = nowUtc,
                    UpdatedAt = nowUtc
                };

                foreach (var line in order.LineItems)
                {
                    marketplaceOrder.Items.Add(new MarketplaceOrderItem
                    {
                        TenantId = tenantId,
                        ClientId = clientId,
                        Provider = MarketplaceProvider.Shopify,
                        MlItemId = line.Id.ToString(),
                        MlVariationId = line.VariantId?.ToString(),
                        SabrVariantSku = string.IsNullOrWhiteSpace(line.Sku) ? null : line.Sku,
                        Quantity = line.Quantity,
                        MappingState = string.IsNullOrWhiteSpace(line.Sku) ? "UNMAPPED" : "MAPPED_BY_SKU",
                        RawJson = JsonSerializer.Serialize(line),
                        CreatedAt = nowUtc,
                        UpdatedAt = nowUtc
                    });
                }

                _dbContext.MarketplaceOrders.Add(marketplaceOrder);
            }
            else
            {
                existing.Status = order.FinancialStatus;
                existing.RawJson = JsonSerializer.Serialize(order);
                existing.UpdatedAt = nowUtc;
            }
        }

        connection.LastSyncAt = nowUtc;
        connection.UpdatedAt = nowUtc;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<ShopifySyncResult>.Success(new ShopifySyncResult
        {
            OrdersFetched = orders.Count,
            SyncedAt = connection.LastSyncAt!.Value
        });
    }

    public async Task SyncAllConnectionsAsync(CancellationToken cancellationToken = default)
    {
        var connections = await _dbContext.TenantMarketplaceConnections
            .Where(c => c.Provider == MarketplaceProvider.Shopify
                        && c.AccessToken != null
                        && c.AccessToken != string.Empty)
            .ToListAsync(cancellationToken);

        foreach (var conn in connections)
        {
            await SyncNowAsync(conn.TenantId, conn.ClientId, cancellationToken);
        }
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

// ── Result types ──────────────────────────────────────────────────────────────

public sealed class ShopifyStatusResult
{
    public bool IsConnected { get; set; }
    public string? Shop { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
}

public sealed class ShopifySyncResult
{
    public int OrdersFetched { get; set; }
    public DateTimeOffset SyncedAt { get; set; }
}
