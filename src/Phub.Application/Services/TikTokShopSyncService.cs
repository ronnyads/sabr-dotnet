using System.Net;
using System.Text.Json;
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

public sealed class TikTokShopSyncService
{
    private const int SaveBatchSize = 25;

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

        var metadataHydrated = false;
        if (string.IsNullOrWhiteSpace(connection.ShopCipher))
        {
            metadataHydrated = await TryHydrateConnectionMetadataAsync(connection, accessToken, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(connection.ShopCipher))
        {
            _logger.LogWarning(
                "TikTok Shop sync aborted because connection has no usable shop cipher. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} operation={Operation}",
                tenantId,
                clientId,
                connection.SellerId,
                "sync_orders");
            return ServiceResult<TikTokShopSyncResult>.Failure(
                ServiceErrorCodes.TikTokShopReconnectRequired,
                "connection",
                "A autorizacao foi concluida, mas o TikTok Shop nao retornou os dados completos da loja. Reconecte a conta para sincronizar pedidos.");
        }

        _logger.LogInformation(
            "TikTok Shop sync started. tenantId={TenantId} clientId={ClientId} from={From} to={To} sellerId={SellerId} hasShopCipher={HasShopCipher} metadataHydrated={MetadataHydrated}",
            tenantId, clientId, from, to, connection.SellerId, !string.IsNullOrWhiteSpace(connection.ShopCipher), metadataHydrated);

        List<TikTokShopOrderSummary> summaries;
        try
        {
            summaries = await FetchAllOrderSummariesAsync(accessToken, connection.ShopCipher, from, to, cancellationToken);
        }
        catch (TikTokShopApiException ex) when (IsAuthFailure(ex.StatusCode))
        {
            _logger.LogWarning(
                ex,
                "TikTok Shop order search rejected. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} hasShopCipher={HasShopCipher} statusCode={StatusCode} requestId={RequestId} apiCode={ApiCode} apiMessage={ApiMessage} operation={Operation}",
                tenantId,
                clientId,
                connection.SellerId,
                !string.IsNullOrWhiteSpace(connection.ShopCipher),
                ex.StatusCode,
                ex.RequestId,
                ex.ApiCode,
                ex.ApiMessage,
                ex.Operation ?? "search_orders");

            if (await TryHydrateConnectionMetadataAsync(connection, accessToken, cancellationToken))
            {
                var retryResult = await RetryFetchAllOrderSummariesAsync(
                    tenantId,
                    clientId,
                    connection,
                    accessToken,
                    from,
                    to,
                    cancellationToken);
                if (!retryResult.Succeeded)
                {
                    return ServiceResult<TikTokShopSyncResult>.Failure(
                        retryResult.ErrorCode ?? ServiceErrorCodes.TikTokShopReconnectRequired,
                        retryResult.Errors);
                }

                summaries = retryResult.Data ?? [];
            }
            else
            {
                return ServiceResult<TikTokShopSyncResult>.Failure(
                    ServiceErrorCodes.TikTokShopReconnectRequired,
                    "connection",
                    "A sessao TikTok Shop ficou inconsistente para sincronizar pedidos. Reconecte a conta.");
            }
        }
        catch (HttpRequestException ex) when (IsAuthFailure(ex.StatusCode))
        {
            _logger.LogWarning(
                ex,
                "TikTok Shop order search rejected via HTTP exception. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} hasShopCipher={HasShopCipher} statusCode={StatusCode} operation={Operation}",
                tenantId,
                clientId,
                connection.SellerId,
                !string.IsNullOrWhiteSpace(connection.ShopCipher),
                ex.StatusCode,
                "search_orders");

            if (await TryHydrateConnectionMetadataAsync(connection, accessToken, cancellationToken))
            {
                var retryResult = await RetryFetchAllOrderSummariesAsync(
                    tenantId,
                    clientId,
                    connection,
                    accessToken,
                    from,
                    to,
                    cancellationToken);
                if (!retryResult.Succeeded)
                {
                    return ServiceResult<TikTokShopSyncResult>.Failure(
                        retryResult.ErrorCode ?? ServiceErrorCodes.TikTokShopReconnectRequired,
                        retryResult.Errors);
                }

                summaries = retryResult.Data ?? [];
            }
            else
            {
                return ServiceResult<TikTokShopSyncResult>.Failure(
                    ServiceErrorCodes.TikTokShopReconnectRequired,
                    "connection",
                    "A sessao TikTok Shop ficou inconsistente para sincronizar pedidos. Reconecte a conta.");
            }
        }
        catch (TikTokShopApiException ex)
        {
            _logger.LogError(
                ex,
                "TikTok Shop order search failed upstream. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} hasShopCipher={HasShopCipher} statusCode={StatusCode} requestId={RequestId} apiCode={ApiCode} apiMessage={ApiMessage} operation={Operation}",
                tenantId,
                clientId,
                connection.SellerId,
                !string.IsNullOrWhiteSpace(connection.ShopCipher),
                ex.StatusCode,
                ex.RequestId,
                ex.ApiCode,
                ex.ApiMessage,
                ex.Operation ?? "search_orders");
            return ServiceResult<TikTokShopSyncResult>.Failure(
                ServiceErrorCodes.TikTokShopUpstreamError,
                "connection",
                "TikTok Shop indisponivel no momento. Tente novamente.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                ex,
                "TikTok Shop order search failed via HTTP exception. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} hasShopCipher={HasShopCipher} statusCode={StatusCode} operation={Operation}",
                tenantId,
                clientId,
                connection.SellerId,
                !string.IsNullOrWhiteSpace(connection.ShopCipher),
                ex.StatusCode,
                "search_orders");
            return ServiceResult<TikTokShopSyncResult>.Failure(
                ServiceErrorCodes.TikTokShopUpstreamError,
                "connection",
                "TikTok Shop indisponivel no momento. Tente novamente.");
        }

        if (summaries.Count == 0)
        {
            connection.LastSyncAt = to;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return ServiceResult<TikTokShopSyncResult>.Success(new TikTokShopSyncResult());
        }

        List<TikTokShopOrderDetail> details;
        try
        {
            // Fetch details in batches of 50
            details = await FetchOrderDetailsInBatchesAsync(
                accessToken,
                connection.ShopCipher,
                summaries.Select(s => s.OrderId).Where(id => !string.IsNullOrWhiteSpace(id)).ToArray(),
                cancellationToken);
        }
        catch (TikTokShopApiException ex) when (IsAuthFailure(ex.StatusCode))
        {
            _logger.LogWarning(
                ex,
                "TikTok Shop order detail rejected. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} hasShopCipher={HasShopCipher} statusCode={StatusCode} requestId={RequestId} apiCode={ApiCode} apiMessage={ApiMessage} operation={Operation}",
                tenantId,
                clientId,
                connection.SellerId,
                !string.IsNullOrWhiteSpace(connection.ShopCipher),
                ex.StatusCode,
                ex.RequestId,
                ex.ApiCode,
                ex.ApiMessage,
                ex.Operation ?? "get_order_detail");
            return ServiceResult<TikTokShopSyncResult>.Failure(
                ServiceErrorCodes.TikTokShopReconnectRequired,
                "connection",
                "Sessao TikTok Shop invalida ao carregar os detalhes dos pedidos. Reconecte sua conta.");
        }
        catch (HttpRequestException ex) when (IsAuthFailure(ex.StatusCode))
        {
            _logger.LogWarning(
                ex,
                "TikTok Shop order detail rejected via HTTP exception. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} hasShopCipher={HasShopCipher} statusCode={StatusCode} operation={Operation}",
                tenantId,
                clientId,
                connection.SellerId,
                !string.IsNullOrWhiteSpace(connection.ShopCipher),
                ex.StatusCode,
                "get_order_detail");
            return ServiceResult<TikTokShopSyncResult>.Failure(
                ServiceErrorCodes.TikTokShopReconnectRequired,
                "connection",
                "Sessao TikTok Shop invalida ao carregar os detalhes dos pedidos. Reconecte sua conta.");
        }
        catch (TikTokShopApiException ex)
        {
            _logger.LogError(
                ex,
                "TikTok Shop order detail failed upstream. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} hasShopCipher={HasShopCipher} statusCode={StatusCode} requestId={RequestId} apiCode={ApiCode} apiMessage={ApiMessage} operation={Operation}",
                tenantId,
                clientId,
                connection.SellerId,
                !string.IsNullOrWhiteSpace(connection.ShopCipher),
                ex.StatusCode,
                ex.RequestId,
                ex.ApiCode,
                ex.ApiMessage,
                ex.Operation ?? "get_order_detail");
            return ServiceResult<TikTokShopSyncResult>.Failure(
                ServiceErrorCodes.TikTokShopUpstreamError,
                "connection",
                "TikTok Shop indisponivel ao carregar detalhes dos pedidos. Tente novamente.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                ex,
                "TikTok Shop order detail failed via HTTP exception. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} hasShopCipher={HasShopCipher} statusCode={StatusCode} operation={Operation}",
                tenantId,
                clientId,
                connection.SellerId,
                !string.IsNullOrWhiteSpace(connection.ShopCipher),
                ex.StatusCode,
                "get_order_detail");
            return ServiceResult<TikTokShopSyncResult>.Failure(
                ServiceErrorCodes.TikTokShopUpstreamError,
                "connection",
                "TikTok Shop indisponivel ao carregar detalhes dos pedidos. Tente novamente.");
        }

        var autoMappedSkuLookup = await BuildSellerSkuLookupAsync(details, cancellationToken);

        if (connection.SellerId <= 0)
        {
            var resolvedSellerId = details
                .Select(detail => TryParseSellerId(detail.ShopId))
                .FirstOrDefault(value => value > 0);
            if (resolvedSellerId > 0)
            {
                connection.SellerId = resolvedSellerId;
                connection.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

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
        int mappingsCreated = 0;
        int pendingPersistedOrders = 0;

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

                    UpsertOrderItems(
                        existingOrder,
                        detail,
                        tenantId,
                        clientId,
                        connection.SellerId,
                        mappingLookup,
                        autoMappedSkuLookup,
                        ref itemsUpserted,
                        ref mappingsCreated);
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

                    UpsertOrderItems(
                        order,
                        detail,
                        tenantId,
                        clientId,
                        connection.SellerId,
                        mappingLookup,
                        autoMappedSkuLookup,
                        ref itemsUpserted,
                        ref mappingsCreated);
                    _dbContext.MarketplaceOrders.Add(order);
                    existingOrderMap[order.MlOrderId] = order;
                    ordersUpserted++;
                }

                pendingPersistedOrders++;
                if (pendingPersistedOrders >= SaveBatchSize)
                {
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    pendingPersistedOrders = 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to map TikTok order {OrderId}", detail.OrderId);
            }
        }

        await TrySyncShipmentsAsync(
            tenantId,
            clientId,
            connection,
            accessToken,
            from,
            to,
            existingOrderMap,
            cancellationToken);

        connection.LastSyncAt = to;
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "TikTok Shop sync completed. tenantId={TenantId} clientId={ClientId} ordersUpserted={Orders} itemsUpserted={Items} mappingsCreated={MappingsCreated} sellerId={SellerId}",
            tenantId, clientId, ordersUpserted, itemsUpserted, mappingsCreated, connection.SellerId);

        return ServiceResult<TikTokShopSyncResult>.Success(new TikTokShopSyncResult
        {
            OrdersUpserted = ordersUpserted,
            ItemsUpserted = itemsUpserted
        });
    }

    private async Task<ServiceResult<List<TikTokShopOrderSummary>>> RetryFetchAllOrderSummariesAsync(
        string tenantId,
        Guid clientId,
        TenantMarketplaceConnection connection,
        string accessToken,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connection.ShopCipher))
        {
            return ServiceResult<List<TikTokShopOrderSummary>>.Failure(
                ServiceErrorCodes.TikTokShopReconnectRequired,
                "connection",
                "A sessao TikTok Shop ficou inconsistente para sincronizar pedidos. Reconecte a conta.");
        }

        try
        {
            _logger.LogInformation(
                "TikTok Shop sync retrying order search after metadata hydration. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} hasShopCipher={HasShopCipher} operation={Operation}",
                tenantId,
                clientId,
                connection.SellerId,
                !string.IsNullOrWhiteSpace(connection.ShopCipher),
                "sync_orders");

            var summaries = await FetchAllOrderSummariesAsync(accessToken, connection.ShopCipher, from, to, cancellationToken);
            return ServiceResult<List<TikTokShopOrderSummary>>.Success(summaries);
        }
        catch (TikTokShopApiException ex) when (IsAuthFailure(ex.StatusCode))
        {
            _logger.LogWarning(
                ex,
                "TikTok Shop order search still rejected after metadata hydration. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} hasShopCipher={HasShopCipher} statusCode={StatusCode} requestId={RequestId} operation={Operation}",
                tenantId,
                clientId,
                connection.SellerId,
                !string.IsNullOrWhiteSpace(connection.ShopCipher),
                ex.StatusCode,
                ex.RequestId,
                ex.Operation ?? "search_orders");
            return ServiceResult<List<TikTokShopOrderSummary>>.Failure(
                ServiceErrorCodes.TikTokShopReconnectRequired,
                "connection",
                "A sessao TikTok Shop ficou inconsistente para sincronizar pedidos. Reconecte a conta.");
        }
        catch (HttpRequestException ex) when (IsAuthFailure(ex.StatusCode))
        {
            _logger.LogWarning(
                ex,
                "TikTok Shop order search still rejected after metadata hydration via HTTP exception. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} hasShopCipher={HasShopCipher} statusCode={StatusCode} operation={Operation}",
                tenantId,
                clientId,
                connection.SellerId,
                !string.IsNullOrWhiteSpace(connection.ShopCipher),
                ex.StatusCode,
                "search_orders");
            return ServiceResult<List<TikTokShopOrderSummary>>.Failure(
                ServiceErrorCodes.TikTokShopReconnectRequired,
                "connection",
                "A sessao TikTok Shop ficou inconsistente para sincronizar pedidos. Reconecte a conta.");
        }
    }

    private void UpsertOrderItems(
        MarketplaceOrder order,
        TikTokShopOrderDetail detail,
        string tenantId,
        Guid clientId,
        long sellerId,
        Dictionary<string, string> mappingLookup,
        IReadOnlyDictionary<string, string> sellerSkuLookup,
        ref int itemsUpserted,
        ref int mappingsCreated)
    {
        foreach (var lineItem in detail.LineItems)
        {
            if (lineItem.Quantity <= 0)
            {
                _logger.LogWarning(
                    "TikTok Shop line item skipped because it has non-positive quantity. orderId={OrderId} lineItemId={LineItemId} productId={ProductId} skuId={SkuId} quantity={Quantity}",
                    detail.OrderId,
                    lineItem.Id,
                    lineItem.ProductId,
                    lineItem.SkuId,
                    lineItem.Quantity);
                continue;
            }

            var productId = lineItem.ProductId ?? string.Empty;
            var skuId = lineItem.SkuId ?? string.Empty;
            var mapKey = $"{productId}|{skuId}";
            mappingLookup.TryGetValue(mapKey, out var sabrSku);

            if (string.IsNullOrWhiteSpace(sabrSku))
            {
                var normalizedSellerSku = NormalizeSellerSku(lineItem.SellerSku);
                if (normalizedSellerSku != null &&
                    sellerSkuLookup.TryGetValue(normalizedSellerSku, out var autoMappedSku))
                {
                    sabrSku = autoMappedSku;
                    if (!string.IsNullOrWhiteSpace(productId))
                    {
                        EnsureAutoMapping(
                            tenantId,
                            clientId,
                            productId,
                            skuId,
                            sabrSku,
                            mappingLookup,
                            ref mappingsCreated);
                    }
                }
            }

            var existing = order.Items.FirstOrDefault(i => i.MlItemId == productId && i.MlVariationId == skuId);
            if (existing != null)
            {
                existing.Quantity = lineItem.Quantity;
                existing.SabrVariantSku = sabrSku;
                existing.MappingState = sabrSku != null ? MarketplaceMappingStates.Mapped : MarketplaceMappingStates.Unmapped;
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
                    MappingState = sabrSku != null ? MarketplaceMappingStates.Mapped : MarketplaceMappingStates.Unmapped,
                    RawJson = JsonSerializer.Serialize(lineItem)
                });
                itemsUpserted++;
            }
        }
    }

    private void EnsureAutoMapping(
        string tenantId,
        Guid clientId,
        string tikTokItemId,
        string? tikTokSkuId,
        string sabrVariantSku,
        Dictionary<string, string> mappingLookup,
        ref int mappingsCreated)
    {
        var mapKey = $"{tikTokItemId}|{tikTokSkuId ?? string.Empty}";
        if (mappingLookup.ContainsKey(mapKey))
        {
            return;
        }

        _dbContext.TenantMarketplaceListingMaps.Add(new TenantMarketplaceListingMap
        {
            TenantId = tenantId,
            ClientId = clientId,
            Provider = MarketplaceProvider.TikTokShop,
            MlItemId = tikTokItemId,
            MlVariationId = string.IsNullOrWhiteSpace(tikTokSkuId) ? null : tikTokSkuId,
            SabrVariantSku = sabrVariantSku
        });

        mappingLookup[mapKey] = sabrVariantSku;
        mappingsCreated++;
    }

    private async Task<IReadOnlyDictionary<string, string>> BuildSellerSkuLookupAsync(
        IReadOnlyCollection<TikTokShopOrderDetail> details,
        CancellationToken cancellationToken)
    {
        var sellerSkus = details
            .SelectMany(detail => detail.LineItems)
            .Select(item => NormalizeSellerSku(item.SellerSku))
            .Where(value => value != null)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (sellerSkus.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var variants = await _dbContext.ProductVariants
            .AsNoTracking()
            .Where(variant => sellerSkus.Contains(variant.VariantSku))
            .Select(variant => variant.VariantSku)
            .ToListAsync(cancellationToken);

        return variants.ToDictionary(value => value, value => value, StringComparer.Ordinal);
    }

    private static string? NormalizeSellerSku(string? sellerSku)
    {
        return string.IsNullOrWhiteSpace(sellerSku)
            ? null
            : Phub.Domain.ValueObjects.Sku.Normalize(sellerSku);
    }

    private static long TryParseSellerId(string? rawShopId)
    {
        return long.TryParse(rawShopId, out var parsed) ? parsed : 0L;
    }

    private async Task<List<TikTokShopOrderSummary>> FetchAllOrderSummariesAsync(
        string accessToken,
        string? shopCipher,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var all = new List<TikTokShopOrderSummary>();
        string? pageToken = null;

        do
        {
            var response = await _apiClient.SearchOrdersAsync(
                accessToken, _options.AppKey, _options.AppSecret, from, to, shopCipher, pageToken, cancellationToken);

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
        string? shopCipher,
        string[] orderIds,
        CancellationToken cancellationToken)
    {
        var all = new List<TikTokShopOrderDetail>();

        foreach (var batch in orderIds.Chunk(50))
        {
            var response = await _apiClient.GetOrderDetailAsync(
                accessToken, _options.AppKey, _options.AppSecret, batch, shopCipher, cancellationToken);

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

    private async Task TrySyncShipmentsAsync(
        string tenantId,
        Guid clientId,
        TenantMarketplaceConnection connection,
        string accessToken,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyDictionary<string, MarketplaceOrder> orderLookup,
        CancellationToken cancellationToken)
    {
        try
        {
            var packageSummaries = await FetchAllPackagesAsync(accessToken, connection.ShopCipher, from, to, cancellationToken);
            if (packageSummaries.Count == 0)
            {
                return;
            }

            var packageIds = packageSummaries
                .Select(item => item.PackageId)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var packageDetails = new List<TikTokShopPackageDetail>();
            foreach (var packageId in packageIds)
            {
                var response = await _apiClient.GetPackageDetailAsync(
                    accessToken,
                    _options.AppKey,
                    _options.AppSecret,
                    packageId,
                    connection.ShopCipher,
                    cancellationToken);

                if (response.IsSuccess && response.Data != null && !string.IsNullOrWhiteSpace(response.Data.PackageId))
                {
                    packageDetails.Add(response.Data);
                }
            }

            if (packageDetails.Count == 0)
            {
                return;
            }

            var existingShipments = await _dbContext.MarketplaceShipments
                .Where(item => item.TenantId == tenantId
                               && item.ClientId == clientId
                               && item.Provider == MarketplaceProvider.TikTokShop
                               && packageIds.Contains(item.ShipmentId))
                .ToDictionaryAsync(item => item.ShipmentId, StringComparer.Ordinal, cancellationToken);

            foreach (var packageDetail in packageDetails)
            {
                if (!existingShipments.TryGetValue(packageDetail.PackageId, out var shipment))
                {
                    shipment = new MarketplaceShipment
                    {
                        TenantId = tenantId,
                        ClientId = clientId,
                        Provider = MarketplaceProvider.TikTokShop,
                        SellerId = connection.SellerId,
                        ShipmentId = packageDetail.PackageId,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    existingShipments[shipment.ShipmentId] = shipment;
                    _dbContext.MarketplaceShipments.Add(shipment);
                }

                shipment.SellerId = connection.SellerId;
                shipment.MlOrderId = packageDetail.OrderInfoList.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.OrderId))?.OrderId;
                shipment.Status = FormatPackageStatus(packageDetail.PackageStatus);
                shipment.Substatus = packageDetail.PackageFreezeStatus > 0 ? $"freeze_{packageDetail.PackageFreezeStatus}" : null;
                shipment.ShippingMode = FormatDeliveryOption(packageDetail.DeliveryOption);
                shipment.LogisticType = NormalizeNullable(packageDetail.ShippingProvider)
                    ?? (packageDetail.PickUpType > 0 ? $"pickup_{packageDetail.PickUpType}" : null);
                shipment.TrackingNumber = NormalizeNullable(packageDetail.TrackingNumber);
                shipment.TrackingMethod = NormalizeNullable(packageDetail.ShippingProvider);
                shipment.TrackingUrl = null;
                shipment.ShipByDeadlineAt = FromUnixTimeSeconds(packageDetail.PickUpEndTime);
                shipment.ShippedAt = InferShippedAt(packageDetail);
                shipment.UpdatedAt = DateTimeOffset.UtcNow;

                try
                {
                    var documentResponse = await _apiClient.GetPackageShippingDocumentAsync(
                        accessToken,
                        _options.AppKey,
                        _options.AppSecret,
                        packageDetail.PackageId,
                        connection.ShopCipher,
                        cancellationToken: cancellationToken);

                    if (documentResponse.IsSuccess && !string.IsNullOrWhiteSpace(documentResponse.Data?.DocUrl))
                    {
                        shipment.LabelSourceUrl = documentResponse.Data.DocUrl;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "TikTok Shop package shipping document probe failed. tenantId={TenantId} clientId={ClientId} packageId={PackageId}",
                        tenantId,
                        clientId,
                        packageDetail.PackageId);
                }

                if (!string.IsNullOrWhiteSpace(shipment.MlOrderId)
                    && orderLookup.TryGetValue(shipment.MlOrderId, out var order))
                {
                    order.ShipmentId ??= shipment.ShipmentId;
                    order.ShippingMode ??= shipment.ShippingMode;
                    order.LogisticType ??= shipment.LogisticType;
                    order.ShipByDeadlineAt ??= shipment.ShipByDeadlineAt;
                    order.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
        }
        catch (TikTokShopApiException ex)
        {
            _logger.LogWarning(
                ex,
                "TikTok Shop package sync skipped after upstream rejection. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} statusCode={StatusCode} requestId={RequestId} operation={Operation}",
                tenantId,
                clientId,
                connection.SellerId,
                ex.StatusCode,
                ex.RequestId,
                ex.Operation ?? "packages");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "TikTok Shop package sync skipped after HTTP failure. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} statusCode={StatusCode}",
                tenantId,
                clientId,
                connection.SellerId,
                ex.StatusCode);
        }
    }

    private async Task<List<TikTokShopPackageSummary>> FetchAllPackagesAsync(
        string accessToken,
        string? shopCipher,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var all = new List<TikTokShopPackageSummary>();
        string? cursor = null;

        do
        {
            var response = await _apiClient.SearchPackagesAsync(
                accessToken,
                _options.AppKey,
                _options.AppSecret,
                from,
                to,
                shopCipher,
                cursor,
                cancellationToken);

            if (!response.IsSuccess || response.Data?.PackageList == null)
            {
                break;
            }

            all.AddRange(response.Data.PackageList);
            cursor = response.Data.More ? response.Data.NextCursor : null;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return all;
    }

    private async Task<bool> TryHydrateConnectionMetadataAsync(
        TenantMarketplaceConnection connection,
        string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var shops = await _apiClient.GetAuthorizedShopsAsync(
                accessToken,
                _options.AppKey,
                _options.AppSecret,
                cancellationToken);

            var matchingShop = shops
                .Where(shop => !string.IsNullOrWhiteSpace(shop.ShopCipher))
                .OrderByDescending(shop => string.Equals(shop.ShopCipher, connection.ShopCipher, StringComparison.Ordinal))
                .ThenByDescending(shop => string.Equals(shop.ShopId, connection.SellerId > 0 ? connection.SellerId.ToString() : null, StringComparison.Ordinal))
                .FirstOrDefault();

            if (matchingShop is null)
            {
                _logger.LogWarning(
                    "TikTok Shop metadata hydration returned no usable authorized shop. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} hasShopCipher={HasShopCipher} operation={Operation}",
                    connection.TenantId,
                    connection.ClientId,
                    connection.SellerId,
                    !string.IsNullOrWhiteSpace(connection.ShopCipher),
                    "authorized_shops");
                return false;
            }

            var resolvedSellerId = TryParseSellerId(matchingShop.ShopId);
            connection.ShopCipher = NormalizeNullable(matchingShop.ShopCipher);
            if (resolvedSellerId > 0)
            {
                connection.SellerId = resolvedSellerId;
            }

            var displayName = string.IsNullOrWhiteSpace(matchingShop.SellerName)
                ? matchingShop.ShopName
                : matchingShop.SellerName;
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                connection.Nickname = displayName.Trim();
            }

            connection.UpdatedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "TikTok Shop metadata hydrated from authorized shops. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} hasShopCipher={HasShopCipher} operation={Operation}",
                connection.TenantId,
                connection.ClientId,
                connection.SellerId,
                !string.IsNullOrWhiteSpace(connection.ShopCipher),
                "authorized_shops");
            return !string.IsNullOrWhiteSpace(connection.ShopCipher);
        }
        catch (TikTokShopApiException ex) when (IsAuthFailure(ex.StatusCode))
        {
            _logger.LogWarning(
                ex,
                "TikTok Shop metadata hydration was rejected. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} hasShopCipher={HasShopCipher} statusCode={StatusCode} requestId={RequestId} operation={Operation}",
                connection.TenantId,
                connection.ClientId,
                connection.SellerId,
                !string.IsNullOrWhiteSpace(connection.ShopCipher),
                ex.StatusCode,
                ex.RequestId,
                ex.Operation ?? "authorized_shops");
            return false;
        }
        catch (HttpRequestException ex) when (IsAuthFailure(ex.StatusCode))
        {
            _logger.LogWarning(
                ex,
                "TikTok Shop metadata hydration was rejected via HTTP exception. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} hasShopCipher={HasShopCipher} statusCode={StatusCode} operation={Operation}",
                connection.TenantId,
                connection.ClientId,
                connection.SellerId,
                !string.IsNullOrWhiteSpace(connection.ShopCipher),
                ex.StatusCode,
                "authorized_shops");
            return false;
        }
        catch (TikTokShopApiException ex)
        {
            _logger.LogWarning(
                ex,
                "TikTok Shop metadata hydration failed upstream. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} hasShopCipher={HasShopCipher} statusCode={StatusCode} requestId={RequestId} operation={Operation}",
                connection.TenantId,
                connection.ClientId,
                connection.SellerId,
                !string.IsNullOrWhiteSpace(connection.ShopCipher),
                ex.StatusCode,
                ex.RequestId,
                ex.Operation ?? "authorized_shops");
            return false;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "TikTok Shop metadata hydration failed via HTTP exception. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} hasShopCipher={HasShopCipher} statusCode={StatusCode} operation={Operation}",
                connection.TenantId,
                connection.ClientId,
                connection.SellerId,
                !string.IsNullOrWhiteSpace(connection.ShopCipher),
                ex.StatusCode,
                "authorized_shops");
            return false;
        }
    }

    private static bool IsAuthFailure(HttpStatusCode? statusCode)
        => statusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FormatDeliveryOption(int deliveryOption)
        => deliveryOption <= 0 ? null : $"delivery_{deliveryOption}";

    private static string FormatPackageStatus(int packageStatus)
        => packageStatus <= 0 ? "PACKAGE_CREATED" : $"PACKAGE_{packageStatus}";

    private static DateTimeOffset? FromUnixTimeSeconds(long? unixSeconds)
        => unixSeconds.HasValue && unixSeconds.Value > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value)
            : null;

    private static DateTimeOffset? InferShippedAt(TikTokShopPackageDetail packageDetail)
    {
        return packageDetail.PackageStatus >= 140
            ? FromUnixTimeSeconds(packageDetail.UpdateTime)
            : null;
    }
}

public sealed class TikTokShopSyncResult
{
    public int OrdersUpserted { get; set; }
    public int ItemsUpserted { get; set; }
}
