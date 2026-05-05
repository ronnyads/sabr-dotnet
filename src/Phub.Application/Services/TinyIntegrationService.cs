using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class TinyIntegrationService
{
    private readonly IAppDbContext _dbContext;
    private readonly ITinyErpApiClient _tinyApiClient;
    private readonly TinyOAuthService _tinyOAuthService;
    private readonly MarketplaceOrderNumberService _orderNumberService;
    private readonly MarketplaceOrderInventoryService _inventoryService;
    private readonly MarketplaceOrderMappingService _mappingService;
    private readonly ILogger<TinyIntegrationService> _logger;

    public TinyIntegrationService(
        IAppDbContext dbContext,
        ITinyErpApiClient tinyApiClient,
        TinyOAuthService tinyOAuthService,
        MarketplaceOrderNumberService orderNumberService,
        MarketplaceOrderInventoryService inventoryService,
        MarketplaceOrderMappingService mappingService,
        ILogger<TinyIntegrationService> logger)
    {
        _dbContext = dbContext;
        _tinyApiClient = tinyApiClient;
        _tinyOAuthService = tinyOAuthService;
        _orderNumberService = orderNumberService;
        _inventoryService = inventoryService;
        _mappingService = mappingService;
        _logger = logger;
    }

    // ── Status ────────────────────────────────────────────────────────────────

    public async Task<ServiceResult<TinyIntegrationStatusResult>> GetClientStatusAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty)
        {
            return ServiceResult<TinyIntegrationStatusResult>.Failure(new[]
            {
                new ValidationError("context", "Invalid tenant/client context")
            });
        }

        var connection = await _dbContext.TenantMarketplaceConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.TenantId == tenantId
                        && item.ClientId == clientId
                        && item.Provider == MarketplaceProvider.TinyErp,
                cancellationToken);

        if (connection == null)
        {
            return ServiceResult<TinyIntegrationStatusResult>.Success(new TinyIntegrationStatusResult
            {
                IsConnected = false
            });
        }

        return ServiceResult<TinyIntegrationStatusResult>.Success(new TinyIntegrationStatusResult
        {
            IsConnected = true,
            CompanyName = connection.Nickname,
            ConnectedAt = connection.CreatedAt.UtcDateTime,
            LastSyncAt = connection.LastSyncAt?.UtcDateTime
        });
    }

    // ── Disconnect ────────────────────────────────────────────────────────────

    public async Task<ServiceResult<bool>> DisconnectAsync(
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

        var connections = await _dbContext.TenantMarketplaceConnections
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == MarketplaceProvider.TinyErp)
            .ToListAsync(cancellationToken);

        if (connections.Count == 0)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("connection", "No Tiny ERP connection found")
            });
        }

        _dbContext.TenantMarketplaceConnections.RemoveRange(connections);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

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
                        && o.Provider == MarketplaceProvider.TinyErp)
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
                            && s.Provider == MarketplaceProvider.TinyErp)
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
                        && e.Provider == MarketplaceProvider.TinyErp)
            .ToListAsync(cancellationToken);
        _dbContext.MarketplaceEventLogs.RemoveRange(eventLogs);

        var slaRules = await _dbContext.TenantMarketplaceSlaRules
            .Where(r => r.TenantId == tenantId
                        && r.ClientId == clientId
                        && r.Provider == MarketplaceProvider.TinyErp)
            .ToListAsync(cancellationToken);
        _dbContext.TenantMarketplaceSlaRules.RemoveRange(slaRules);

        var allConnections = await _dbContext.TenantMarketplaceConnections
            .Where(c => c.TenantId == tenantId
                        && c.ClientId == clientId
                        && c.Provider == MarketplaceProvider.TinyErp)
            .ToListAsync(cancellationToken);
        _dbContext.TenantMarketplaceConnections.RemoveRange(allConnections);

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Tiny ERP integration reset. tenantId={TenantId} clientId={ClientId} orders={Orders}",
            tenantId, clientId, orderIds.Count);

        return ServiceResult<bool>.Success(true);
    }

    // ── Sync Orders ───────────────────────────────────────────────────────────

    public async Task<ServiceResult<TinySyncResult>> SyncOrdersAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var connectionResult = await GetConnectionAsync(tenantId, clientId, cancellationToken);
        if (connectionResult.connection == null)
        {
            return ServiceResult<TinySyncResult>.Failure(new[]
            {
                new ValidationError("connection", connectionResult.error ?? "Tiny ERP not connected")
            });
        }

        var connection = connectionResult.connection;
        string accessToken;
        try
        {
            accessToken = await _tinyOAuthService.GetValidAccessTokenAsync(connection, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return ServiceResult<TinySyncResult>.Failure(new[]
            {
                new ValidationError("auth", "TINY_AUTH_INVALID")
            });
        }

        var result = new TinySyncResult();
        var page = 1;
        bool hasMore;

        do
        {
            TinyPagedResult<TinyOrderResult> pageResult;
            try
            {
                pageResult = await _tinyApiClient.ListOrdersAsync(accessToken, page, ct: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TinyERP SyncOrders failed on page {Page}", page);
                result.Errors.Add($"Page {page}: {ex.Message}");
                break;
            }

            foreach (var tinyOrder in pageResult.Dados)
            {
                try
                {
                    await UpsertOrderAsync(tenantId, clientId, connection.SellerId, tinyOrder, result, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "TinyERP failed to upsert order {OrderId}", tinyOrder.Id);
                    result.Errors.Add($"Order {tinyOrder.Id}: {ex.Message}");
                }
            }

            hasMore = page < pageResult.Paginas;
            page++;
        } while (hasMore);

        connection.LastSyncAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<TinySyncResult>.Success(result);
    }

    private async Task UpsertOrderAsync(
        string tenantId,
        Guid clientId,
        long sellerId,
        TinyOrderResult tinyOrder,
        TinySyncResult syncResult,
        CancellationToken cancellationToken)
    {
        var mlOrderId = tinyOrder.Id.ToString();
        var nowUtc = DateTimeOffset.UtcNow;

        var existing = await _dbContext.MarketplaceOrders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(
                o => o.TenantId == tenantId
                     && o.ClientId == clientId
                     && o.Provider == MarketplaceProvider.TinyErp
                     && o.MlOrderId == mlOrderId,
                cancellationToken);

        var order = existing;
        if (order != null)
        {
            if (string.IsNullOrWhiteSpace(order.InternalOrderNumber))
            {
                await _orderNumberService.EnsureOrderNumberAsync(order, cancellationToken);
            }

            syncResult.Updated++;
        }
        else
        {
            order = new MarketplaceOrder
            {
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.TinyErp,
                SellerId = sellerId,
                MlOrderId = mlOrderId,
                Status = tinyOrder.Situacao,
                PaidAt = tinyOrder.DataPedido.HasValue ? new DateTimeOffset(tinyOrder.DataPedido.Value, TimeSpan.Zero) : null
            };
            await _orderNumberService.EnsureOrderNumberAsync(order, cancellationToken);
            _dbContext.MarketplaceOrders.Add(order);
            syncResult.Imported++;
        }

        order.Status = tinyOrder.Situacao;
        order.SellerId = sellerId;
        order.PaidAt = tinyOrder.DataPedido.HasValue ? new DateTimeOffset(tinyOrder.DataPedido.Value, TimeSpan.Zero) : null;
        order.RawJson = System.Text.Json.JsonSerializer.Serialize(tinyOrder);
        order.UpdatedAt = nowUtc;

        foreach (var tinyItem in tinyOrder.Itens)
        {
            var itemId = tinyItem.Id.ToString();
            var resolution = await _mappingService.ResolveImportedItemAsync(
                tenantId,
                clientId,
                MarketplaceProvider.TinyErp,
                sellerId,
                null,
                itemId,
                null,
                tinyItem.Codigo,
                cancellationToken);

            var orderItem = order.Items.FirstOrDefault(item => item.MlItemId == itemId);
            if (orderItem == null)
            {
                orderItem = new MarketplaceOrderItem
                {
                    MarketplaceOrderId = order.Id,
                    TenantId = tenantId,
                    ClientId = clientId,
                    Provider = MarketplaceProvider.TinyErp,
                    SellerId = sellerId,
                    MlItemId = itemId,
                    CreatedAt = nowUtc
                };
                order.Items.Add(orderItem);
            }

            orderItem.SellerId = sellerId;
            orderItem.SabrVariantSku = resolution.SabrVariantSku;
            orderItem.Quantity = (int)tinyItem.Quantidade;
            orderItem.MappingState = resolution.MappingState;
            orderItem.RawJson = System.Text.Json.JsonSerializer.Serialize(tinyItem);
            orderItem.UpdatedAt = nowUtc;
        }

        await _inventoryService.ReconcileReservationsAsync(
            order,
            sellerId,
            reservationTtlHours: 24,
            cancellationToken);
    }

    // ── Labels ────────────────────────────────────────────────────────────────

    public async Task<ServiceResult<byte[]>> GetOrFetchLabelAsync(
        string tenantId,
        Guid clientId,
        long tinyOrderId,
        CancellationToken cancellationToken = default)
    {
        var connectionResult = await GetConnectionAsync(tenantId, clientId, cancellationToken);
        if (connectionResult.connection == null)
        {
            return ServiceResult<byte[]>.Failure(new[]
            {
                new ValidationError("connection", connectionResult.error ?? "Tiny ERP not connected")
            });
        }

        string accessToken;
        try
        {
            accessToken = await _tinyOAuthService.GetValidAccessTokenAsync(connectionResult.connection, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return ServiceResult<byte[]>.Failure(new[]
            {
                new ValidationError("auth", "TINY_AUTH_INVALID")
            });
        }

        var order = await _tinyApiClient.GetOrderAsync(accessToken, tinyOrderId, cancellationToken);
        if (order?.Envio?.IdAgrupamento == null)
        {
            return ServiceResult<byte[]>.Failure(new[]
            {
                new ValidationError("shipment", "No agrupamento found for this order")
            });
        }

        try
        {
            var bytes = await _tinyApiClient.GetExpedicaoLabelsAsync(accessToken, order.Envio.IdAgrupamento.Value, cancellationToken);
            return ServiceResult<byte[]>.Success(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TinyERP GetExpedicaoLabels failed for agrupamento {Id}", order.Envio.IdAgrupamento);
            return ServiceResult<byte[]>.Failure(new[]
            {
                new ValidationError("labels", ex.Message)
            });
        }
    }

    // ── Invoice ───────────────────────────────────────────────────────────────

    public async Task<ServiceResult<TinyInvoiceResult>> GenerateInvoiceAsync(
        string tenantId,
        Guid clientId,
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var connectionResult = await GetConnectionAsync(tenantId, clientId, cancellationToken);
        if (connectionResult.connection == null)
        {
            return ServiceResult<TinyInvoiceResult>.Failure(new[]
            {
                new ValidationError("connection", connectionResult.error ?? "Tiny ERP not connected")
            });
        }

        var order = await _dbContext.MarketplaceOrders
            .AsNoTracking()
            .FirstOrDefaultAsync(
                o => o.Id == orderId
                     && o.TenantId == tenantId
                     && o.ClientId == clientId
                     && o.Provider == MarketplaceProvider.TinyErp,
                cancellationToken);

        if (order == null)
        {
            return ServiceResult<TinyInvoiceResult>.Failure(new[]
            {
                new ValidationError("orderId", "Order not found")
            });
        }

        if (!long.TryParse(order.MlOrderId, out var tinyOrderId))
        {
            return ServiceResult<TinyInvoiceResult>.Failure(new[]
            {
                new ValidationError("orderId", "Invalid Tiny order ID")
            });
        }

        string accessToken;
        try
        {
            accessToken = await _tinyOAuthService.GetValidAccessTokenAsync(connectionResult.connection, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return ServiceResult<TinyInvoiceResult>.Failure(new[]
            {
                new ValidationError("auth", "TINY_AUTH_INVALID")
            });
        }

        try
        {
            var nota = await _tinyApiClient.GenerateInvoiceAsync(accessToken, tinyOrderId, cancellationToken);
            var xmlUrl = await TryGetNoteLinkAsync(accessToken, nota.Id, cancellationToken);

            return ServiceResult<TinyInvoiceResult>.Success(new TinyInvoiceResult
            {
                NoteId = nota.Id,
                Numero = nota.Numero,
                ChaveAcesso = nota.ChaveAcesso,
                XmlUrl = xmlUrl,
                Situacao = nota.Situacao
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TinyERP GenerateInvoice failed for order {OrderId}", tinyOrderId);
            return ServiceResult<TinyInvoiceResult>.Failure(new[]
            {
                new ValidationError("invoice", ex.Message)
            });
        }
    }

    private async Task<string?> TryGetNoteLinkAsync(string accessToken, long noteId, CancellationToken cancellationToken)
    {
        try
        {
            return await _tinyApiClient.GetNoteLinkAsync(accessToken, noteId, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    // ── Catalog Sync ──────────────────────────────────────────────────────────

    public async Task<ServiceResult<TinyCatalogSyncResult>> SyncProductCatalogAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var connectionResult = await GetConnectionAsync(tenantId, clientId, cancellationToken);
        if (connectionResult.connection == null)
        {
            return ServiceResult<TinyCatalogSyncResult>.Failure(new[]
            {
                new ValidationError("connection", connectionResult.error ?? "Tiny ERP not connected")
            });
        }

        string accessToken;
        try
        {
            accessToken = await _tinyOAuthService.GetValidAccessTokenAsync(connectionResult.connection, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return ServiceResult<TinyCatalogSyncResult>.Failure(new[]
            {
                new ValidationError("auth", "TINY_AUTH_INVALID")
            });
        }

        var syncResult = new TinyCatalogSyncResult();
        var page = 1;
        bool hasMore;

        do
        {
            TinyPagedResult<TinyProductResult> pageResult;
            try
            {
                pageResult = await _tinyApiClient.ListProductsAsync(accessToken, page, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TinyERP SyncProductCatalog failed on page {Page}", page);
                break;
            }

            foreach (var tinyProduct in pageResult.Dados)
            {
                var sku = tinyProduct.Codigo?.Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(sku))
                {
                    syncResult.Skipped++;
                    continue;
                }

                var variant = await _dbContext.ProductVariants
                    .AsNoTracking()
                    .FirstOrDefaultAsync(v => v.VariantSku == sku, cancellationToken);

                if (variant != null)
                {
                    syncResult.Linked++;
                }
                else
                {
                    syncResult.Unlinked++;
                }
            }

            hasMore = page < pageResult.Paginas;
            page++;
        } while (hasMore);

        return ServiceResult<TinyCatalogSyncResult>.Success(syncResult);
    }

    // ── Push Stock ────────────────────────────────────────────────────────────

    public async Task PushStockToTinyAsync(string tenantId, Guid clientId, string sku)
    {
        try
        {
            var connection = await _dbContext.TenantMarketplaceConnections
                .FirstOrDefaultAsync(
                    item => item.TenantId == tenantId
                            && item.ClientId == clientId
                            && item.Provider == MarketplaceProvider.TinyErp);

            if (connection == null) return;

            string accessToken;
            try
            {
                accessToken = await _tinyOAuthService.GetValidAccessTokenAsync(connection);
            }
            catch (InvalidOperationException)
            {
                _logger.LogWarning("TinyERP PushStock: auth invalid for tenant {TenantId} client {ClientId}", tenantId, clientId);
                return;
            }

            var variant = await _dbContext.ProductVariants
                .AsNoTracking()
                .FirstOrDefaultAsync(v => v.VariantSku == sku);

            if (variant == null) return;

            // Find Tiny product by iterating catalog to match by SKU
            var page = 1;
            long? tinyProductId = null;
            bool hasMore;
            do
            {
                var pageResult = await _tinyApiClient.ListProductsAsync(accessToken, page);
                var match = pageResult.Dados.FirstOrDefault(p =>
                    string.Equals(p.Codigo?.Trim(), sku, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    tinyProductId = match.Id;
                    break;
                }

                hasMore = page < pageResult.Paginas;
                page++;
            } while (hasMore);

            if (tinyProductId == null)
            {
                _logger.LogWarning("TinyERP PushStock: product with SKU {Sku} not found in Tiny", sku);
                return;
            }

            await _tinyApiClient.UpdateProductStockAsync(accessToken, tinyProductId.Value, variant.AvailableStock);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TinyERP PushStockToTinyAsync failed for SKU {Sku}", sku);
        }
    }

    // ── Update Order Dispatch ─────────────────────────────────────────────────

    public async Task UpdateOrderDispatchAsync(Guid orderId, string trackingCode, string carrier)
    {
        try
        {
            var order = await _dbContext.MarketplaceOrders
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == orderId && o.Provider == MarketplaceProvider.TinyErp);

            if (order == null) return;

            var connection = await _dbContext.TenantMarketplaceConnections
                .FirstOrDefaultAsync(
                    item => item.TenantId == order.TenantId
                            && item.ClientId == order.ClientId
                            && item.Provider == MarketplaceProvider.TinyErp);

            if (connection == null) return;

            string accessToken;
            try
            {
                accessToken = await _tinyOAuthService.GetValidAccessTokenAsync(connection);
            }
            catch (InvalidOperationException)
            {
                _logger.LogWarning("TinyERP UpdateOrderDispatch: auth invalid for order {OrderId}", orderId);
                return;
            }

            if (!long.TryParse(order.MlOrderId, out var tinyOrderId)) return;

            await _tinyApiClient.UpdateOrderDespachoAsync(accessToken, tinyOrderId, trackingCode, carrier);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TinyERP UpdateOrderDispatchAsync failed for order {OrderId}", orderId);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(TenantMarketplaceConnection? connection, string? error)> GetConnectionAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.TenantMarketplaceConnections
            .FirstOrDefaultAsync(
                item => item.TenantId == tenantId
                        && item.ClientId == clientId
                        && item.Provider == MarketplaceProvider.TinyErp,
                cancellationToken);

        return connection == null
            ? (null, "No Tiny ERP connection found for this client")
            : (connection, null);
    }
}
