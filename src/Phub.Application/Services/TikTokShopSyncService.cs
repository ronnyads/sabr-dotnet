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
    private const int DetailBatchSize = 50;
    private const string SyncOrdersTopic = "sync_orders";
    private const string SyncStatusResourceId = "tiktokshop";
    private const string SyncHealthHealthy = "healthy";
    private const string SyncHealthDegraded = "degraded";
    private const string SyncHealthBlocked = "blocked";
    private const string SyncBlockingReasonDetailApiV2Required = "detail_api_v2_required";
    private const string SyncBlockingReasonNoImportedItems = "no_imported_items";
    private const string TikTokDeprecatedOrderDetailApiCode = "36009034";

    private readonly IAppDbContext _dbContext;
    private readonly ITikTokShopApiClient _apiClient;
    private readonly TikTokShopOAuthService _oauthService;
    private readonly MarketplaceOrderNumberService _orderNumberService;
    private readonly MarketplaceOrderInventoryService _inventoryService;
    private readonly MarketplaceOrderMappingService _mappingService;
    private readonly TikTokShopOptions _options;
    private readonly ILogger<TikTokShopSyncService> _logger;

    public TikTokShopSyncService(
        IAppDbContext dbContext,
        ITikTokShopApiClient apiClient,
        TikTokShopOAuthService oauthService,
        MarketplaceOrderNumberService orderNumberService,
        MarketplaceOrderInventoryService inventoryService,
        MarketplaceOrderMappingService mappingService,
        IOptions<TikTokShopOptions> options,
        ILogger<TikTokShopSyncService> logger)
    {
        _dbContext = dbContext;
        _apiClient = apiClient;
        _oauthService = oauthService;
        _orderNumberService = orderNumberService;
        _inventoryService = inventoryService;
        _mappingService = mappingService;
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
        from = await ExpandSyncWindowForBrokenOrdersAsync(tenantId, clientId, from, cancellationToken);
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
            await RecordSyncStatusAsync(
                tenantId,
                clientId,
                connection.SellerId,
                MarketplaceEventStatuses.Failed,
                SyncHealthBlocked,
                ServiceErrorCodes.TikTokShopReconnectRequired,
                "A autorizacao foi concluida, mas o TikTok Shop nao retornou os dados completos da loja. Reconecte a conta para sincronizar pedidos.",
                "connection_reconnect_required",
                null,
                cancellationToken);
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
            await RecordSyncStatusAsync(
                tenantId,
                clientId,
                connection.SellerId,
                MarketplaceEventStatuses.Failed,
                SyncHealthDegraded,
                ServiceErrorCodes.TikTokShopUpstreamError,
                "TikTok Shop indisponivel no momento. Tente novamente.",
                "upstream_error",
                ex,
                cancellationToken);
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
            await RecordSyncStatusAsync(
                tenantId,
                clientId,
                connection.SellerId,
                MarketplaceEventStatuses.Failed,
                SyncHealthDegraded,
                ServiceErrorCodes.TikTokShopUpstreamError,
                "TikTok Shop indisponivel no momento. Tente novamente.",
                "upstream_error",
                null,
                cancellationToken);
            return ServiceResult<TikTokShopSyncResult>.Failure(
                ServiceErrorCodes.TikTokShopUpstreamError,
                "connection",
                "TikTok Shop indisponivel no momento. Tente novamente.");
        }

        var repairedItems = 0;
        if (summaries.Count == 0)
        {
            try
            {
                repairedItems = await RecoverOrdersWithMissingItemsAsync(
                    tenantId,
                    clientId,
                    connection,
                    accessToken,
                    cancellationToken);
            }
            catch (TikTokShopApiException ex) when (IsDeprecatedOrderDetailApi(ex))
            {
                _logger.LogWarning(
                    ex,
                    "TikTok Shop repair sync blocked because order detail requires V2. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} requestId={RequestId} apiCode={ApiCode}",
                    tenantId,
                    clientId,
                    connection.SellerId,
                    ex.RequestId,
                    ex.ApiCode);
                await RecordSyncStatusAsync(
                    tenantId,
                    clientId,
                    connection.SellerId,
                    MarketplaceEventStatuses.Failed,
                    SyncHealthBlocked,
                    ServiceErrorCodes.TikTokShopOrderDetailV2Required,
                    "O TikTok Shop bloqueou o detalhe dos pedidos no endpoint legado. A integracao precisa consultar o payload V2 para importar os itens.",
                    SyncBlockingReasonDetailApiV2Required,
                    ex,
                    cancellationToken);
                return ServiceResult<TikTokShopSyncResult>.Failure(
                    ServiceErrorCodes.TikTokShopOrderDetailV2Required,
                    "connection",
                    "O TikTok Shop bloqueou o detalhe dos pedidos no endpoint legado. Tente sincronizar novamente em alguns minutos.");
            }

            connection.LastSyncAt = to;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await RecordHealthyOrBrokenSyncStatusAsync(
                tenantId,
                clientId,
                connection.SellerId,
                repairedItems,
                cancellationToken);
            return ServiceResult<TikTokShopSyncResult>.Success(new TikTokShopSyncResult
            {
                ItemsUpserted = repairedItems
            });
        }

        var details = summaries
            .Where(summary => summary.LineItems.Count > 0)
            .Select(summary => summary.ToOrderDetail())
            .ToList();

        var orderIdsRequiringDetail = summaries
            .Where(summary => summary.LineItems.Count == 0)
            .Select(summary => summary.OrderId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        try
        {
            if (orderIdsRequiringDetail.Length > 0)
            {
                details.AddRange(await FetchOrderDetailsInBatchesAsync(
                    accessToken,
                    connection.ShopCipher,
                    connection.SellerId > 0 ? connection.SellerId.ToString() : null,
                    orderIdsRequiringDetail,
                    cancellationToken));
            }
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
            await RecordSyncStatusAsync(
                tenantId,
                clientId,
                connection.SellerId,
                MarketplaceEventStatuses.Failed,
                SyncHealthBlocked,
                ServiceErrorCodes.TikTokShopReconnectRequired,
                "Sessao TikTok Shop invalida ao carregar os detalhes dos pedidos. Reconecte sua conta.",
                "connection_reconnect_required",
                ex,
                cancellationToken);
            return ServiceResult<TikTokShopSyncResult>.Failure(
                ServiceErrorCodes.TikTokShopReconnectRequired,
                "connection",
                "Sessao TikTok Shop invalida ao carregar os detalhes dos pedidos. Reconecte sua conta.");
        }
        catch (TikTokShopApiException ex) when (IsDeprecatedOrderDetailApi(ex))
        {
            _logger.LogWarning(
                ex,
                "TikTok Shop order detail blocked because V1 detail API was deprecated. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} requestId={RequestId} apiCode={ApiCode} apiMessage={ApiMessage}",
                tenantId,
                clientId,
                connection.SellerId,
                ex.RequestId,
                ex.ApiCode,
                ex.ApiMessage);
            await RecordSyncStatusAsync(
                tenantId,
                clientId,
                connection.SellerId,
                MarketplaceEventStatuses.Failed,
                SyncHealthBlocked,
                ServiceErrorCodes.TikTokShopOrderDetailV2Required,
                "O TikTok Shop recusou o detalhe dos pedidos no endpoint legado. A sincronizacao nao conseguiu importar os itens do pedido.",
                SyncBlockingReasonDetailApiV2Required,
                ex,
                cancellationToken);
            return ServiceResult<TikTokShopSyncResult>.Failure(
                ServiceErrorCodes.TikTokShopOrderDetailV2Required,
                "connection",
                "O TikTok Shop recusou o detalhe dos pedidos no endpoint legado. Os itens do pedido ainda nao puderam ser importados.");
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
            await RecordSyncStatusAsync(
                tenantId,
                clientId,
                connection.SellerId,
                MarketplaceEventStatuses.Failed,
                SyncHealthBlocked,
                ServiceErrorCodes.TikTokShopReconnectRequired,
                "Sessao TikTok Shop invalida ao carregar os detalhes dos pedidos. Reconecte sua conta.",
                "connection_reconnect_required",
                null,
                cancellationToken);
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
            await RecordSyncStatusAsync(
                tenantId,
                clientId,
                connection.SellerId,
                MarketplaceEventStatuses.Failed,
                SyncHealthDegraded,
                ServiceErrorCodes.TikTokShopUpstreamError,
                "TikTok Shop indisponivel ao carregar detalhes dos pedidos. Tente novamente.",
                "upstream_error",
                ex,
                cancellationToken);
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
            await RecordSyncStatusAsync(
                tenantId,
                clientId,
                connection.SellerId,
                MarketplaceEventStatuses.Failed,
                SyncHealthDegraded,
                ServiceErrorCodes.TikTokShopUpstreamError,
                "TikTok Shop indisponivel ao carregar detalhes dos pedidos. Tente novamente.",
                "upstream_error",
                null,
                cancellationToken);
            return ServiceResult<TikTokShopSyncResult>.Failure(
                ServiceErrorCodes.TikTokShopUpstreamError,
                "connection",
                "TikTok Shop indisponivel ao carregar detalhes dos pedidos. Tente novamente.");
        }

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
        int pendingPersistedOrders = 0;

        foreach (var detail in details)
        {
            try
            {
                var rawJson = JsonSerializer.Serialize(detail);

                if (existingOrderMap.TryGetValue(detail.OrderId, out var existingOrder))
                {
                    if (string.IsNullOrWhiteSpace(existingOrder.InternalOrderNumber))
                    {
                        await _orderNumberService.EnsureOrderNumberAsync(existingOrder, cancellationToken);
                    }

                    existingOrder.Status = detail.OrderStatus;
                    existingOrder.UpdatedAt = DateTimeOffset.UtcNow;
                    existingOrder.RawJson = rawJson;
                    if (detail.PaidTime.HasValue && detail.PaidTime.Value > 0)
                    {
                        existingOrder.PaidAt = DateTimeOffset.FromUnixTimeSeconds(detail.PaidTime.Value);
                    }

                    itemsUpserted += await UpsertOrderItemsAsync(
                        existingOrder,
                        detail,
                        tenantId,
                        clientId,
                        connection.SellerId,
                        cancellationToken);
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
                    await _orderNumberService.EnsureOrderNumberAsync(order, cancellationToken);

                    if (detail.PaidTime.HasValue && detail.PaidTime.Value > 0)
                    {
                        order.PaidAt = DateTimeOffset.FromUnixTimeSeconds(detail.PaidTime.Value);
                    }

                    itemsUpserted += await UpsertOrderItemsAsync(
                        order,
                        detail,
                        tenantId,
                        clientId,
                        connection.SellerId,
                        cancellationToken);
                    _dbContext.MarketplaceOrders.Add(order);
                    existingOrderMap[order.MlOrderId] = order;
                    ordersUpserted++;
                }

                if (existingOrderMap.TryGetValue(detail.OrderId, out var currentOrder))
                {
                    await _inventoryService.ReconcileReservationsAsync(
                        currentOrder,
                        connection.SellerId,
                        reservationTtlHours: 24,
                        cancellationToken);
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

        try
        {
            repairedItems += await RecoverOrdersWithMissingItemsAsync(
                tenantId,
                clientId,
                connection,
                accessToken,
                cancellationToken);
        }
        catch (TikTokShopApiException ex) when (IsDeprecatedOrderDetailApi(ex))
        {
            _logger.LogWarning(
                ex,
                "TikTok Shop repair pass blocked because order detail requires V2. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} requestId={RequestId} apiCode={ApiCode}",
                tenantId,
                clientId,
                connection.SellerId,
                ex.RequestId,
                ex.ApiCode);
            await RecordSyncStatusAsync(
                tenantId,
                clientId,
                connection.SellerId,
                MarketplaceEventStatuses.Failed,
                SyncHealthBlocked,
                ServiceErrorCodes.TikTokShopOrderDetailV2Required,
                "O TikTok Shop recusou o detalhe dos pedidos no endpoint legado. Ainda existem pedidos sem itens importados.",
                SyncBlockingReasonDetailApiV2Required,
                ex,
                cancellationToken);
            return ServiceResult<TikTokShopSyncResult>.Failure(
                ServiceErrorCodes.TikTokShopOrderDetailV2Required,
                "connection",
                "O TikTok Shop recusou o detalhe dos pedidos no endpoint legado. Ainda existem pedidos sem itens importados.");
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
        await RecordHealthyOrBrokenSyncStatusAsync(
            tenantId,
            clientId,
            connection.SellerId,
            itemsUpserted + repairedItems,
            cancellationToken);

        _logger.LogInformation(
            "TikTok Shop sync completed. tenantId={TenantId} clientId={ClientId} ordersUpserted={Orders} itemsUpserted={Items} repairedItems={RepairedItems} mappingsCreated={MappingsCreated} sellerId={SellerId}",
            tenantId, clientId, ordersUpserted, itemsUpserted, repairedItems, 0, connection.SellerId);

        return ServiceResult<TikTokShopSyncResult>.Success(new TikTokShopSyncResult
        {
            OrdersUpserted = ordersUpserted,
            ItemsUpserted = itemsUpserted + repairedItems
        });
    }

    public async Task SyncAllConnectionsAsync(CancellationToken cancellationToken = default)
    {
        var connections = await _dbContext.TenantMarketplaceConnections
            .AsNoTracking()
            .Where(c => c.Provider == MarketplaceProvider.TikTokShop
                        && ((c.AccessToken != null && c.AccessToken != string.Empty)
                            || (c.RefreshToken != null && c.RefreshToken != string.Empty)))
            .Select(c => new { c.TenantId, c.ClientId })
            .ToListAsync(cancellationToken);

        foreach (var connection in connections)
        {
            try
            {
                await SyncOrdersAsync(connection.TenantId, connection.ClientId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "TikTok Shop sync-all failed for connection. tenantId={TenantId} clientId={ClientId}",
                    connection.TenantId,
                    connection.ClientId);
            }
        }
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

    private async Task<DateTimeOffset> ExpandSyncWindowForBrokenOrdersAsync(
        string tenantId,
        Guid clientId,
        DateTimeOffset from,
        CancellationToken cancellationToken)
    {
        var oldestBrokenImportedAt = await _dbContext.MarketplaceOrders
            .AsNoTracking()
            .Where(order => order.TenantId == tenantId
                            && order.ClientId == clientId
                            && order.Provider == MarketplaceProvider.TikTokShop
                            && !order.Items.Any())
            .OrderBy(order => order.ImportedAt)
            .Select(order => (DateTimeOffset?)order.ImportedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (!oldestBrokenImportedAt.HasValue)
        {
            return from;
        }

        var expandedFrom = oldestBrokenImportedAt.Value.AddDays(-1);
        var minimumAllowed = DateTimeOffset.UtcNow.AddDays(-30);
        if (expandedFrom < minimumAllowed)
        {
            expandedFrom = minimumAllowed;
        }

        return expandedFrom < from ? expandedFrom : from;
    }

    private async Task RecordHealthyOrBrokenSyncStatusAsync(
        string tenantId,
        Guid clientId,
        long sellerId,
        int itemsUpserted,
        CancellationToken cancellationToken)
    {
        var ordersMissingItemsCount = await GetOrdersMissingItemsCountAsync(tenantId, clientId, cancellationToken);
        var syncHealth = ordersMissingItemsCount > 0 ? SyncHealthDegraded : SyncHealthHealthy;
        var blockingReason = ordersMissingItemsCount > 0 ? SyncBlockingReasonNoImportedItems : null;
        var message = ordersMissingItemsCount > 0
            ? $"Ainda existem {ordersMissingItemsCount} pedido(s) TikTok sem itens importados para mapear."
            : "Sincronizacao TikTok concluida com itens importados.";

        await RecordSyncStatusAsync(
            tenantId,
            clientId,
            sellerId,
            MarketplaceEventStatuses.Processed,
            syncHealth,
            ordersMissingItemsCount > 0 ? SyncBlockingReasonNoImportedItems : null,
            message,
            blockingReason,
            null,
            cancellationToken,
            itemsUpserted,
            ordersMissingItemsCount);
    }

    private async Task<int> GetOrdersMissingItemsCountAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken)
    {
        return await _dbContext.MarketplaceOrders
            .AsNoTracking()
            .Where(order => order.TenantId == tenantId
                            && order.ClientId == clientId
                            && order.Provider == MarketplaceProvider.TikTokShop
                            && !order.Items.Any())
            .CountAsync(cancellationToken);
    }

    private async Task RecordSyncStatusAsync(
        string tenantId,
        Guid clientId,
        long sellerId,
        string status,
        string syncHealth,
        string? errorCode,
        string message,
        string? blockingReason,
        TikTokShopApiException? exception,
        CancellationToken cancellationToken,
        int? itemsUpserted = null,
        int? ordersMissingItemsCount = null)
    {
        var dedupeKey = $"{SyncOrdersTopic}:{tenantId}:{clientId:D}:{MarketplaceProvider.TikTokShop}";
        var existing = await _dbContext.MarketplaceEventLogs.FirstOrDefaultAsync(
            item => item.DedupeKey == dedupeKey,
            cancellationToken);

        var payload = JsonSerializer.Serialize(new
        {
            syncHealth,
            errorCode,
            blockingReason,
            message,
            requestId = exception?.RequestId,
            apiCode = exception?.ApiCode,
            apiMessage = exception?.ApiMessage,
            operation = exception?.Operation,
            itemsUpserted,
            ordersMissingItemsCount,
            recordedAt = DateTimeOffset.UtcNow
        });

        if (existing is null)
        {
            existing = new MarketplaceEventLog
            {
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.TikTokShop,
                SellerId = sellerId,
                Topic = SyncOrdersTopic,
                ResourceId = SyncStatusResourceId,
                DedupeKey = dedupeKey
            };
            _dbContext.MarketplaceEventLogs.Add(existing);
        }

        existing.SellerId = sellerId;
        existing.Status = status;
        existing.Attempts += 1;
        existing.ProcessedAt = status == MarketplaceEventStatuses.Processed ? DateTimeOffset.UtcNow : existing.ProcessedAt;
        existing.LastErrorAt = status == MarketplaceEventStatuses.Failed ? DateTimeOffset.UtcNow : null;
        existing.LastError = status == MarketplaceEventStatuses.Failed ? message : null;
        existing.PayloadJson = payload;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> UpsertOrderItemsAsync(
        MarketplaceOrder order,
        TikTokShopOrderDetail detail,
        string tenantId,
        Guid clientId,
        long sellerId,
        CancellationToken cancellationToken)
    {
        var itemsUpserted = 0;
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
            var resolution = await _mappingService.ResolveImportedItemAsync(
                tenantId,
                clientId,
                MarketplaceProvider.TikTokShop,
                sellerId,
                null,
                productId,
                string.IsNullOrWhiteSpace(skuId) ? null : skuId,
                lineItem.SellerSku,
                cancellationToken);

            var existing = order.Items.FirstOrDefault(i => i.MlItemId == productId && i.MlVariationId == skuId);
            if (existing != null)
            {
                existing.Quantity = lineItem.Quantity;
                existing.SabrVariantSku = resolution.SabrVariantSku;
                existing.MappingState = resolution.MappingState;
                existing.RawJson = JsonSerializer.Serialize(lineItem);
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                var newItem = new MarketplaceOrderItem
                {
                    MarketplaceOrderId = order.Id,
                    TenantId = tenantId,
                    ClientId = clientId,
                    Provider = MarketplaceProvider.TikTokShop,
                    SellerId = sellerId,
                    MlItemId = productId,
                    MlVariationId = skuId,
                    SabrVariantSku = resolution.SabrVariantSku,
                    Quantity = lineItem.Quantity,
                    MappingState = resolution.MappingState,
                    RawJson = JsonSerializer.Serialize(lineItem),
                    MarketplaceOrder = order
                };

                order.Items.Add(newItem);
                _dbContext.MarketplaceOrderItems.Add(newItem);
                itemsUpserted++;
            }
        }

        if (detail.LineItems.Count == 0)
        {
            _logger.LogWarning(
                "TikTok Shop order detail returned no line items. tenantId={TenantId} clientId={ClientId} orderId={OrderId} provider={Provider} sellerId={SellerId} importedItemCount={ImportedItemCount}",
                tenantId,
                clientId,
                detail.OrderId,
                MarketplaceProvider.TikTokShop,
                sellerId,
                itemsUpserted);
        }

        return itemsUpserted;
    }

    private async Task<int> RecoverOrdersWithMissingItemsAsync(
        string tenantId,
        Guid clientId,
        TenantMarketplaceConnection connection,
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connection.ShopCipher))
        {
            return 0;
        }

        var brokenOrders = await _dbContext.MarketplaceOrders
            .Include(order => order.Items)
            .Where(order => order.TenantId == tenantId
                            && order.ClientId == clientId
                            && order.Provider == MarketplaceProvider.TikTokShop
                            && !order.Items.Any())
            .OrderByDescending(order => order.ImportedAt)
            .ToListAsync(cancellationToken);

        if (brokenOrders.Count == 0)
        {
            return 0;
        }

        var repairedItems = 0;
        var pendingPersistedOrders = 0;
        var orderLookup = brokenOrders.ToDictionary(order => order.MlOrderId, StringComparer.Ordinal);

        foreach (var batch in orderLookup.Keys.Chunk(DetailBatchSize))
        {
            var response = await _apiClient.GetOrderDetailAsync(
                accessToken,
                _options.AppKey,
                _options.AppSecret,
                batch,
                connection.ShopCipher,
                connection.SellerId > 0 ? connection.SellerId.ToString() : null,
                cancellationToken);

            foreach (var detail in response.Data?.Orders ?? [])
            {
                if (!orderLookup.TryGetValue(detail.OrderId, out var order))
                {
                    continue;
                }

                repairedItems += await UpsertOrderItemsAsync(
                    order,
                    detail,
                    tenantId,
                    clientId,
                    connection.SellerId,
                    cancellationToken);

                await _inventoryService.ReconcileReservationsAsync(
                    order,
                    connection.SellerId,
                    reservationTtlHours: 24,
                    cancellationToken);
                order.UpdatedAt = DateTimeOffset.UtcNow;

                pendingPersistedOrders++;
                if (pendingPersistedOrders >= SaveBatchSize)
                {
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    pendingPersistedOrders = 0;
                }
            }
        }

        if (pendingPersistedOrders > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        if (repairedItems > 0)
        {
            _logger.LogInformation(
                "TikTok Shop repaired orders without imported items. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} brokenOrders={BrokenOrders} repairedItems={RepairedItems}",
                tenantId,
                clientId,
                connection.SellerId,
                brokenOrders.Count,
                repairedItems);
        }

        return repairedItems;
    }

    private static long TryParseSellerId(string? rawShopId)
    {
        return long.TryParse(rawShopId, out var parsed) ? parsed : 0L;
    }

    public async Task<ServiceResult<TikTokShopShipmentHydrationResult>> EnsureOrderShipmentsAsync(
        Guid orderId,
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.MarketplaceOrders.FirstOrDefaultAsync(
            item => item.Id == orderId
                    && item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.TikTokShop,
            cancellationToken);
        if (order == null)
        {
            return ServiceResult<TikTokShopShipmentHydrationResult>.NotFound("orderId", "TikTok order not found.");
        }

        var tokenResult = await _oauthService.GetValidAccessTokenAsync(tenantId, clientId, cancellationToken);
        if (!tokenResult.Succeeded || string.IsNullOrWhiteSpace(tokenResult.Data))
        {
            return ServiceResult<TikTokShopShipmentHydrationResult>.Failure(tokenResult.ErrorCode ?? ServiceErrorCodes.ValidationError, tokenResult.Errors);
        }

        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.TikTokShop,
            cancellationToken);
        if (connection == null)
        {
            return ServiceResult<TikTokShopShipmentHydrationResult>.Failure(
                ServiceErrorCodes.TikTokShopNotConnected,
                "connection",
                "TikTok Shop nao esta conectado");
        }

        if (string.IsNullOrWhiteSpace(connection.ShopCipher))
        {
            await TryHydrateConnectionMetadataAsync(connection, tokenResult.Data, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(connection.ShopCipher))
        {
            return ServiceResult<TikTokShopShipmentHydrationResult>.Failure(
                ServiceErrorCodes.TikTokShopReconnectRequired,
                "connection",
                "A sessao TikTok Shop nao possui shop cipher para consultar pacotes.");
        }

        var from = order.ImportedAt <= DateTimeOffset.UtcNow
            ? order.ImportedAt
            : DateTimeOffset.UtcNow.AddDays(-7);
        var to = DateTimeOffset.UtcNow;
        try
        {
            var packageSummaries = await FetchPackagesInWindowsAsync(
                tokenResult.Data,
                connection.ShopCipher,
                from,
                to,
                windowSize: TimeSpan.FromDays(7),
                cancellationToken);
            if (packageSummaries.Count == 0)
            {
                return ServiceResult<TikTokShopShipmentHydrationResult>.Success(new TikTokShopShipmentHydrationResult());
            }

            var packageIds = packageSummaries
                .Select(item => item.PackageId)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var packageDetails = await FetchPackageDetailsAsync(
                tokenResult.Data,
                connection.ShopCipher,
                packageIds,
                cancellationToken);
            var matchedDetails = packageDetails
                .Where(item => item.OrderInfoList.Any(info => string.Equals(info.OrderId, order.MlOrderId, StringComparison.Ordinal)))
                .ToList();

            if (matchedDetails.Count == 0)
            {
                return ServiceResult<TikTokShopShipmentHydrationResult>.Success(new TikTokShopShipmentHydrationResult());
            }

            var orderLookup = new Dictionary<string, MarketplaceOrder>(StringComparer.Ordinal)
            {
                [order.MlOrderId] = order
            };
            var hydrated = await UpsertShipmentsAsync(
                tenantId,
                clientId,
                connection,
                tokenResult.Data,
                matchedDetails,
                orderLookup,
                cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            var hydratedPackageIds = matchedDetails
                .Select(item => item.PackageId)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var labelsReleased = await _dbContext.MarketplaceShipments
                .AsNoTracking()
                .CountAsync(
                    item => item.TenantId == tenantId
                            && item.ClientId == clientId
                            && item.Provider == MarketplaceProvider.TikTokShop
                            && hydratedPackageIds.Contains(item.ShipmentId)
                            && !string.IsNullOrWhiteSpace(item.LabelSourceUrl),
                    cancellationToken);

            return ServiceResult<TikTokShopShipmentHydrationResult>.Success(new TikTokShopShipmentHydrationResult
            {
                ShipmentsUpserted = hydrated,
                LabelsReleased = labelsReleased
            });
        }
        catch (TikTokShopApiException ex)
        {
            _logger.LogWarning(
                ex,
                "TikTok Shop forced shipment hydration failed upstream. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} statusCode={StatusCode} requestId={RequestId} operation={Operation}",
                tenantId,
                clientId,
                connection.SellerId,
                ex.StatusCode,
                ex.RequestId,
                ex.Operation ?? "packages");
            return ServiceResult<TikTokShopShipmentHydrationResult>.Failure(
                ServiceErrorCodes.TikTokShopUpstreamError,
                "connection",
                "TikTok Shop indisponivel ao buscar pacotes do pedido.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "TikTok Shop forced shipment hydration failed via HTTP exception. tenantId={TenantId} clientId={ClientId} sellerId={SellerId} statusCode={StatusCode}",
                tenantId,
                clientId,
                connection.SellerId,
                ex.StatusCode);
            return ServiceResult<TikTokShopShipmentHydrationResult>.Failure(
                ServiceErrorCodes.TikTokShopUpstreamError,
                "connection",
                "TikTok Shop indisponivel ao buscar pacotes do pedido.");
        }
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
        string? shopId,
        string[] orderIds,
        CancellationToken cancellationToken)
    {
        var all = new List<TikTokShopOrderDetail>();

        foreach (var batch in orderIds.Chunk(DetailBatchSize))
        {
            var response = await _apiClient.GetOrderDetailAsync(
                accessToken, _options.AppKey, _options.AppSecret, batch, shopCipher, shopId, cancellationToken);

            if (response.IsSuccess && response.Data?.Orders != null)
            {
                if (response.Data.Orders.Count > 0 && response.Data.Orders.All(order => order.LineItems.Count == 0))
                {
                    _logger.LogWarning(
                        "TikTok Shop GetOrderDetail returned orders without imported items. requestId={RequestId} orderCount={OrderCount} provider={Provider}",
                        response.RequestId,
                        response.Data.Orders.Count,
                        MarketplaceProvider.TikTokShop);
                }

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
            var packageDetails = await FetchPackageDetailsAsync(
                accessToken,
                connection.ShopCipher,
                packageIds,
                cancellationToken);

            if (packageDetails.Count == 0)
            {
                return;
            }
            await UpsertShipmentsAsync(
                tenantId,
                clientId,
                connection,
                accessToken,
                packageDetails,
                orderLookup,
                cancellationToken);
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

    private async Task<List<TikTokShopPackageDetail>> FetchPackageDetailsAsync(
        string accessToken,
        string? shopCipher,
        IReadOnlyCollection<string> packageIds,
        CancellationToken cancellationToken)
    {
        var packageDetails = new List<TikTokShopPackageDetail>();
        foreach (var packageId in packageIds)
        {
            var response = await _apiClient.GetPackageDetailAsync(
                accessToken,
                _options.AppKey,
                _options.AppSecret,
                packageId,
                shopCipher,
                cancellationToken);

            if (response.IsSuccess && response.Data != null && !string.IsNullOrWhiteSpace(response.Data.PackageId))
            {
                packageDetails.Add(response.Data);
            }
            else
            {
                _logger.LogWarning(
                    "TikTok Shop GetPackageDetail returned non-success. packageId={PackageId} code={Code} message={Message}",
                    packageId,
                    response.Code,
                    response.Message);
            }
        }

        return packageDetails;
    }

    private async Task<int> UpsertShipmentsAsync(
        string tenantId,
        Guid clientId,
        TenantMarketplaceConnection connection,
        string accessToken,
        IReadOnlyCollection<TikTokShopPackageDetail> packageDetails,
        IReadOnlyDictionary<string, MarketplaceOrder> orderLookup,
        CancellationToken cancellationToken)
    {
        var packageIds = packageDetails
            .Select(item => item.PackageId)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var existingShipments = await _dbContext.MarketplaceShipments
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == MarketplaceProvider.TikTokShop
                           && packageIds.Contains(item.ShipmentId))
            .ToDictionaryAsync(item => item.ShipmentId, StringComparer.Ordinal, cancellationToken);

        var upserted = 0;
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
            shipment.ShipmentScanCode = string.IsNullOrWhiteSpace(shipment.ShipmentScanCode)
                && !string.IsNullOrWhiteSpace(shipment.MlOrderId)
                && orderLookup.TryGetValue(shipment.MlOrderId, out var scanOrder)
                    ? MarketplaceOrderWorkflow.BuildShipmentScanCode(scanOrder, shipment)
                    : shipment.ShipmentScanCode;
            shipment.UpdatedAt = DateTimeOffset.UtcNow;

            await TryHydrateShippingDocumentAsync(tenantId, clientId, connection, accessToken, packageDetail, shipment, cancellationToken);

            if (!string.IsNullOrWhiteSpace(shipment.MlOrderId)
                && orderLookup.TryGetValue(shipment.MlOrderId, out var order))
            {
                order.ShipmentId ??= shipment.ShipmentId;
                order.ShippingMode ??= shipment.ShippingMode;
                order.LogisticType ??= shipment.LogisticType;
                order.ShipByDeadlineAt ??= shipment.ShipByDeadlineAt;
                order.UpdatedAt = DateTimeOffset.UtcNow;
            }

            upserted++;
        }

        return upserted;
    }

    private async Task TryHydrateShippingDocumentAsync(
        string tenantId,
        Guid clientId,
        TenantMarketplaceConnection connection,
        string accessToken,
        TikTokShopPackageDetail packageDetail,
        MarketplaceShipment shipment,
        CancellationToken cancellationToken)
    {
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
                return;
            }

            _logger.LogInformation(
                "TikTok Shop shipping document still unavailable during shipment hydration. tenantId={TenantId} clientId={ClientId} packageId={PackageId} code={Code} message={Message}",
                tenantId,
                clientId,
                packageDetail.PackageId,
                documentResponse.Code,
                documentResponse.Message);
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

    private async Task<List<TikTokShopPackageSummary>> FetchPackagesInWindowsAsync(
        string accessToken,
        string? shopCipher,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan windowSize,
        CancellationToken cancellationToken)
    {
        var all = new List<TikTokShopPackageSummary>();
        var cursor = from;
        while (cursor < to)
        {
            var windowEnd = cursor.Add(windowSize);
            if (windowEnd > to)
            {
                windowEnd = to;
            }

            var batch = await FetchAllPackagesAsync(accessToken, shopCipher, cursor, windowEnd, cancellationToken);
            all.AddRange(batch);
            cursor = windowEnd;
        }

        return all
            .GroupBy(item => item.PackageId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
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

    private static bool IsDeprecatedOrderDetailApi(TikTokShopApiException exception)
        => string.Equals(exception.ApiCode, TikTokDeprecatedOrderDetailApiCode, StringComparison.Ordinal)
           || (!string.IsNullOrWhiteSpace(exception.ApiMessage)
               && exception.ApiMessage.Contains("upgrade to V2", StringComparison.OrdinalIgnoreCase));

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

public sealed class TikTokShopShipmentHydrationResult
{
    public int ShipmentsUpserted { get; set; }
    public int LabelsReleased { get; set; }
}
