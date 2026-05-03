using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phub.Application.Abstractions;
using Phub.Application.Options;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class TikTokShopSyncService
{
    private readonly IAppDbContext _dbContext;
    private readonly ITikTokShopApiClient _apiClient;
    private readonly TikTokShopOAuthService _oauthService;
    private readonly TikTokShopOptions _options;
    private readonly ILogger<TikTokShopSyncService> _logger;

    public TikTokShopSyncService(
        IAppDbContext dbContext,
        ITikTokShopApiClient apiClient,
        TikTokShopOAuthService oauthService,
        IOptions<TikTokShopOptions> options,
        ILogger<TikTokShopSyncService> logger)
    {
        _dbContext = dbContext;
        _apiClient = apiClient;
        _oauthService = oauthService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ServiceResult<TikTokShopSyncResult>> SyncOrdersAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var tokenResult = await _oauthService.GetValidAccessTokenAsync(tenantId, clientId, cancellationToken);
        if (!tokenResult.Succeeded || tokenResult.Data == null)
        {
            return ServiceResult<TikTokShopSyncResult>.Failure(tokenResult.Errors);
        }

        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            c => c.TenantId == tenantId && c.ClientId == clientId && c.Provider == MarketplaceProvider.TikTokShop,
            cancellationToken);

        if (connection == null)
        {
            return ServiceResult<TikTokShopSyncResult>.Failure(new[]
            {
                new ValidationError("connection", "TikTok Shop não está conectado")
            });
        }

        var accessToken = tokenResult.Data;
        var from = connection.LastSyncAt?.AddMinutes(-5) ?? DateTimeOffset.UtcNow.AddDays(-7);
        var to = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "TikTok Shop sync started. tenantId={TenantId} clientId={ClientId} from={From} to={To}",
            tenantId, clientId, from, to);

        var summaries = await FetchAllOrderSummariesAsync(accessToken, from, to, cancellationToken);

        if (summaries.Count == 0)
        {
            connection.LastSyncAt = to;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return ServiceResult<TikTokShopSyncResult>.Success(new TikTokShopSyncResult());
        }

        // Fetch details in batches of 50
        var details = await FetchOrderDetailsInBatchesAsync(accessToken, summaries.Select(s => s.OrderId).ToArray(), cancellationToken);

        // Load existing mappings for fast lookup
        var mappings = await _dbContext.TenantMarketplaceListingMaps
            .Where(m => m.TenantId == tenantId && m.ClientId == clientId && m.Provider == MarketplaceProvider.TikTokShop)
            .ToListAsync(cancellationToken);

        var mappingLookup = mappings.ToDictionary(
            m => $"{m.MlItemId}|{m.MlVariationId ?? ""}",
            m => m.SabrVariantSku);

        // Load existing orders for upsert
        var orderIds = details.Select(d => d.OrderId).ToList();
        var existingOrders = await _dbContext.MarketplaceOrders
            .Include(o => o.Items)
            .Where(o => o.TenantId == tenantId && o.ClientId == clientId
                        && o.Provider == MarketplaceProvider.TikTokShop
                        && orderIds.Contains(o.MlOrderId))
            .ToListAsync(cancellationToken);

        var existingOrderMap = existingOrders.ToDictionary(o => o.MlOrderId);

        int ordersUpserted = 0;
        int itemsUpserted = 0;

        foreach (var detail in details)
        {
            try
            {
                var rawJson = JsonSerializer.Serialize(detail);

                if (existingOrderMap.TryGetValue(detail.OrderId, out var existingOrder))
                {
                    existingOrder.Status = detail.OrderStatus;
                    existingOrder.UpdatedAt = DateTimeOffset.UtcNow;
                    existingOrder.RawJson = rawJson;
                    if (detail.PaidTime.HasValue && detail.PaidTime.Value > 0)
                    {
                        existingOrder.PaidAt = DateTimeOffset.FromUnixTimeSeconds(detail.PaidTime.Value);
                    }

                    UpsertOrderItems(existingOrder, detail, tenantId, clientId, connection.SellerId, mappingLookup, ref itemsUpserted);
                }
                else
                {
                    var order = new MarketplaceOrder
                    {
                        TenantId = tenantId,
                        ClientId = clientId,
                        Provider = MarketplaceProvider.TikTokShop,
                        SellerId = connection.SellerId,
                        MlOrderId = detail.OrderId,
                        Status = detail.OrderStatus,
                        ImportedAt = DateTimeOffset.FromUnixTimeSeconds(detail.CreateTime),
                        RawJson = rawJson
                    };

                    if (detail.PaidTime.HasValue && detail.PaidTime.Value > 0)
                    {
                        order.PaidAt = DateTimeOffset.FromUnixTimeSeconds(detail.PaidTime.Value);
                    }

                    UpsertOrderItems(order, detail, tenantId, clientId, connection.SellerId, mappingLookup, ref itemsUpserted);
                    _dbContext.MarketplaceOrders.Add(order);
                    ordersUpserted++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to map TikTok order {OrderId}", detail.OrderId);
            }
        }

        connection.LastSyncAt = to;
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "TikTok Shop sync completed. tenantId={TenantId} clientId={ClientId} ordersUpserted={Orders} itemsUpserted={Items}",
            tenantId, clientId, ordersUpserted, itemsUpserted);

        return ServiceResult<TikTokShopSyncResult>.Success(new TikTokShopSyncResult
        {
            OrdersUpserted = ordersUpserted,
            ItemsUpserted = itemsUpserted
        });
    }

    private void UpsertOrderItems(
        MarketplaceOrder order,
        TikTokShopOrderDetail detail,
        string tenantId,
        Guid clientId,
        long sellerId,
        Dictionary<string, string> mappingLookup,
        ref int itemsUpserted)
    {
        foreach (var lineItem in detail.LineItems)
        {
            var productId = lineItem.ProductId ?? string.Empty;
            var skuId = lineItem.SkuId ?? string.Empty;
            var mapKey = $"{productId}|{skuId}";
            mappingLookup.TryGetValue(mapKey, out var sabrSku);

            var existing = order.Items.FirstOrDefault(i => i.MlItemId == productId && i.MlVariationId == skuId);
            if (existing != null)
            {
                existing.Quantity = lineItem.Quantity;
                existing.SabrVariantSku = sabrSku;
                existing.MappingState = sabrSku != null ? "MAPPED" : "UNMAPPED";
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                order.Items.Add(new MarketplaceOrderItem
                {
                    TenantId = tenantId,
                    ClientId = clientId,
                    Provider = MarketplaceProvider.TikTokShop,
                    SellerId = sellerId,
                    MlItemId = productId,
                    MlVariationId = skuId,
                    SabrVariantSku = sabrSku,
                    Quantity = lineItem.Quantity,
                    MappingState = sabrSku != null ? "MAPPED" : "UNMAPPED",
                    RawJson = JsonSerializer.Serialize(lineItem)
                });
                itemsUpserted++;
            }
        }
    }

    private async Task<List<TikTokShopOrderSummary>> FetchAllOrderSummariesAsync(
        string accessToken,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var all = new List<TikTokShopOrderSummary>();
        string? pageToken = null;

        do
        {
            var response = await _apiClient.SearchOrdersAsync(
                accessToken, _options.AppKey, _options.AppSecret, from, to, pageToken, cancellationToken);

            if (!response.IsSuccess || response.Data?.Orders == null)
            {
                _logger.LogWarning("TikTok Shop SearchOrders returned non-success: {Code} {Message}", response.Code, response.Message);
                break;
            }

            all.AddRange(response.Data.Orders);
            pageToken = response.Data.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return all;
    }

    private async Task<List<TikTokShopOrderDetail>> FetchOrderDetailsInBatchesAsync(
        string accessToken,
        string[] orderIds,
        CancellationToken cancellationToken)
    {
        var all = new List<TikTokShopOrderDetail>();

        foreach (var batch in orderIds.Chunk(50))
        {
            var response = await _apiClient.GetOrderDetailAsync(
                accessToken, _options.AppKey, _options.AppSecret, batch, cancellationToken);

            if (response.IsSuccess && response.Data?.Orders != null)
            {
                all.AddRange(response.Data.Orders);
            }
            else
            {
                _logger.LogWarning("TikTok Shop GetOrderDetail returned non-success: {Code} {Message}", response.Code, response.Message);
            }
        }

        return all;
    }
}

public sealed class TikTokShopSyncResult
{
    public int OrdersUpserted { get; set; }
    public int ItemsUpserted { get; set; }
}
