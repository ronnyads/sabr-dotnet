using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class OrderFulfillmentService
{
    private readonly IAppDbContext _dbContext;
    private readonly MarketplaceShipmentLabelService _labelService;
    private readonly MarketplaceAuditLogService _auditLogService;
    private readonly TinyIntegrationService _tinyIntegrationService;

    public OrderFulfillmentService(
        IAppDbContext dbContext,
        MarketplaceShipmentLabelService labelService,
        MarketplaceAuditLogService auditLogService,
        TinyIntegrationService tinyIntegrationService)
    {
        _dbContext = dbContext;
        _labelService = labelService;
        _auditLogService = auditLogService;
        _tinyIntegrationService = tinyIntegrationService;
    }

    /// <summary>
    /// Lista pedidos do cliente com resumo logístico e marcos internos.
    /// </summary>
    public async Task<PagedResult<MarketplaceOrderListItemResult>> ListClientOrdersAsync(
        string tenantId,
        Guid clientId,
        MarketplaceProvider? provider,
        string? internalStatus,
        string? channelStatus,
        string? legacyStatus,
        string? logisticType,
        int skip,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var safeSkip = Math.Max(0, skip);
        var safeLimit = Math.Min(200, Math.Max(1, limit));

        var query = _dbContext.MarketplaceOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.TenantId == tenantId && o.ClientId == clientId);

        if (provider.HasValue)
            query = query.Where(o => o.Provider == provider.Value);

        if (!string.IsNullOrWhiteSpace(logisticType))
        {
            var normalizedLogisticType = logisticType.Trim().ToLowerInvariant();
            query = query.Where(o => o.LogisticType != null && o.LogisticType.ToLower() == normalizedLogisticType);
        }

        var orders = await query
            .OrderByDescending(o => o.ImportedAt)
            .ToListAsync(cancellationToken);

        var shipmentLookup = await BuildShipmentLookupAsync(orders, cancellationToken);

        var normalizedChannelStatus = NormalizeFilter(channelStatus) ?? NormalizeFilter(legacyStatus);
        var normalizedInternalStatus = NormalizeFilter(internalStatus);
        var filtered = orders
            .Select(order => BuildOrderView(order, GetOrderShipments(order, shipmentLookup)))
            .Where(view => MatchesInternalStatus(view, normalizedInternalStatus)
                           && MatchesChannelStatus(view, normalizedChannelStatus))
            .ToList();

        var total = filtered.Count;
        var items = filtered
            .Skip(safeSkip)
            .Take(safeLimit)
            .Select(MapClientOrderListItem)
            .ToList();

        return new PagedResult<MarketplaceOrderListItemResult>
        {
            Items = items,
            Total = total,
            Skip = safeSkip,
            Limit = safeLimit
        };
    }

    /// <summary>
    /// Retorna um pedido do cliente com itens, shipments e marcos internos.
    /// </summary>
    public async Task<ServiceResult<MarketplaceOrderDetailResult>> GetClientOrderAsync(
        Guid orderId,
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.MarketplaceOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId
                && o.TenantId == tenantId
                && o.ClientId == clientId, cancellationToken);

        if (order == null)
        {
            return ServiceResult<MarketplaceOrderDetailResult>.Failure([
                new ValidationError("orderId", "ORDER_NOT_FOUND")
            ]);
        }

        var variantSkus = order.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.SabrVariantSku))
            .Select(i => i.SabrVariantSku!)
            .Distinct()
            .ToList();
        var products = await _dbContext.ProductVariants
            .AsNoTracking()
            .Where(v => variantSkus.Contains(v.VariantSku))
            .Select(v => new { v.VariantSku, v.Name })
            .ToDictionaryAsync(v => v.VariantSku, v => v.Name, cancellationToken);

        var shipmentLookup = await BuildShipmentLookupAsync([order], cancellationToken);
        var shipments = GetOrderShipments(order, shipmentLookup)
            .Select(MapShipmentResult)
            .ToList();

        var view = BuildOrderView(order, GetOrderShipments(order, shipmentLookup));
        var result = MapClientOrderDetail(view, shipments, order.Items.Select(i => new MarketplaceOrderItemDetailResult
        {
            Id = i.Id,
            MlItemId = i.MlItemId,
            MlVariationId = i.MlVariationId,
            SabrVariantSku = i.SabrVariantSku,
            ProductName = i.SabrVariantSku != null ? products.GetValueOrDefault(i.SabrVariantSku) : null,
            Quantity = i.Quantity,
            MappingState = i.MappingState
        }).ToList());

        return ServiceResult<MarketplaceOrderDetailResult>.Success(result);
    }

    public async Task<PagedResult<AdminOrderListItemResult>> ListAdminOrdersAsync(
        string? status,
        string? internalStatus,
        string? channelStatus,
        string? tenantId,
        MarketplaceProvider? provider,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int skip,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var safeSkip = Math.Max(0, skip);
        var safeLimit = Math.Min(200, Math.Max(1, limit));

        var query = _dbContext.MarketplaceOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            query = query.Where(o => o.TenantId == tenantId);
        }

        if (provider.HasValue)
        {
            query = query.Where(o => o.Provider == provider.Value);
        }

        if (from.HasValue)
        {
            query = query.Where(o => o.CreatedAt >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(o => o.CreatedAt <= to.Value);
        }

        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(cancellationToken);

        var clientIds = orders.Select(o => o.ClientId).Distinct().ToList();
        var clients = await _dbContext.Clients
            .AsNoTracking()
            .Where(c => clientIds.Contains(c.Id))
            .Select(c => new { c.Id, c.AccountName })
            .ToDictionaryAsync(c => c.Id, c => c.AccountName, cancellationToken);

        var shipmentLookup = await BuildShipmentLookupAsync(orders, cancellationToken);
        var normalizedChannelStatus = NormalizeFilter(channelStatus) ?? NormalizeFilter(status);
        var normalizedInternalStatus = NormalizeFilter(internalStatus);

        var filtered = orders
            .Select(order => BuildOrderView(order, GetOrderShipments(order, shipmentLookup)))
            .Where(view => MatchesInternalStatus(view, normalizedInternalStatus)
                           && MatchesChannelStatus(view, normalizedChannelStatus))
            .ToList();

        var total = filtered.Count;
        var items = filtered
            .Skip(safeSkip)
            .Take(safeLimit)
            .Select(view => MapAdminOrderListItem(view, clients.GetValueOrDefault(view.Order.ClientId)))
            .ToList();

        return new PagedResult<AdminOrderListItemResult>
        {
            Items = items,
            Total = total,
            Skip = safeSkip,
            Limit = safeLimit
        };
    }

    public async Task<ServiceResult<AdminOrderDetailResult>> GetAdminOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.MarketplaceOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order == null)
        {
            return ServiceResult<AdminOrderDetailResult>.Failure([
                new ValidationError("orderId", "ORDER_NOT_FOUND")
            ]);
        }

        var clientName = await _dbContext.Clients
            .AsNoTracking()
            .Where(c => c.Id == order.ClientId)
            .Select(c => c.AccountName)
            .FirstOrDefaultAsync(cancellationToken);

        var shipmentLookup = await BuildShipmentLookupAsync([order], cancellationToken);
        var view = BuildOrderView(order, GetOrderShipments(order, shipmentLookup));

        var variantSkus = order.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.SabrVariantSku))
            .Select(i => i.SabrVariantSku!)
            .Distinct()
            .ToList();
        var products = await _dbContext.ProductVariants
            .AsNoTracking()
            .Where(v => variantSkus.Contains(v.VariantSku))
            .Select(v => new { v.VariantSku, v.Name })
            .ToDictionaryAsync(v => v.VariantSku, v => v.Name, cancellationToken);

        var result = new AdminOrderDetailResult
        {
            Id = order.Id,
            TenantId = order.TenantId,
            ClientId = order.ClientId,
            ClientName = clientName,
            Provider = order.Provider,
            SellerId = order.SellerId.ToString(),
            MlOrderId = order.MlOrderId,
            Status = order.Status,
            PaidAt = order.PaidAt,
            SabrPaymentConfirmedAt = order.SabrPaymentConfirmedAt,
            ShipmentId = view.PrimaryShipment?.Shipment.ShipmentId ?? order.ShipmentId,
            ShippingMode = view.PrimaryShipment?.Shipment.ShippingMode ?? order.ShippingMode,
            LogisticType = view.PrimaryShipment?.Shipment.LogisticType ?? order.LogisticType,
            ShipByDeadlineAt = view.PrimaryShipment?.Shipment.ShipByDeadlineAt ?? order.ShipByDeadlineAt,
            HasUnmappedItems = order.Items.Any(i => i.MappingState == MarketplaceMappingStates.Unmapped),
            TotalItems = order.Items.Count,
            HasLabel = view.HasLabel,
            LabelAvailability = view.LabelAvailability,
            RequiresLabelForPayment = view.RequiresLabelForPayment,
            CanMarkPaid = view.CanMarkPaid,
            CurrentInternalStage = view.CurrentInternalStage,
            ChannelStatus = view.ChannelStatus,
            CancellationRequest = view.CancellationRequest,
            InternalFulfillmentSummary = view.InternalSummary,
            RiskFlagsJson = order.RiskFlagsJson,
            ImportedAt = order.ImportedAt,
            Items = order.Items.Select(i => new AdminOrderItemResult
            {
                Id = i.Id,
                MlItemId = i.MlItemId,
                MlVariationId = i.MlVariationId,
                SabrVariantSku = i.SabrVariantSku,
                ProductName = i.SabrVariantSku != null ? products.GetValueOrDefault(i.SabrVariantSku) : null,
                Quantity = i.Quantity,
                ReservedQuantity = i.ReservedQuantity,
                MappingState = i.MappingState
            }).ToList()
        };

        return ServiceResult<AdminOrderDetailResult>.Success(result);
    }

    /// <summary>
    /// Lista etiquetas disponíveis por shipment/pacote do pedido.
    /// </summary>
    public async Task<ServiceResult<List<MarketplaceShipmentLabelListItemResult>>> ListLabelsAsync(
        Guid orderId,
        string? tenantId,
        Guid? clientId,
        CancellationToken cancellationToken = default)
    {
        var orderResult = await GetOrderForAccessAsync(orderId, tenantId, clientId, cancellationToken);
        if (!orderResult.Succeeded || orderResult.Data == null)
        {
            return ServiceResult<List<MarketplaceShipmentLabelListItemResult>>.Failure(orderResult.Errors);
        }

        var shipmentLookup = await BuildShipmentLookupAsync([orderResult.Data], cancellationToken);
        var shipments = GetOrderShipments(orderResult.Data, shipmentLookup);
        if (shipments.Count == 0)
        {
            return ServiceResult<List<MarketplaceShipmentLabelListItemResult>>.Failure([
                new ValidationError("shipmentId", "SHIPMENT_NOT_AVAILABLE")
            ]);
        }

        var items = shipments
            .OrderBy(item => item.Shipment.CreatedAt)
            .Select(item => new MarketplaceShipmentLabelListItemResult
            {
                ShipmentId = item.Shipment.ShipmentId,
                HasLabel = MarketplaceOrderWorkflow.HasOperationalLabel(item.Shipment),
                LabelAvailability = MarketplaceOrderWorkflow.ResolveLabelAvailability(item.Shipment),
                ShippingProvider = ResolveShippingProvider(item.Shipment),
                TrackingNumber = item.Shipment.TrackingNumber,
                Status = item.Shipment.Status
            })
            .ToList();

        return ServiceResult<List<MarketplaceShipmentLabelListItemResult>>.Success(items);
    }

    /// <summary>
    /// Retorna a etiqueta de envio para um pedido, suportando múltiplos shipments.
    /// </summary>
    public async Task<ServiceResult<MarketplaceShipmentLabelDownloadResult>> GetLabelAsync(
        Guid orderId,
        string? tenantId,
        Guid? clientId,
        string? shipmentId = null,
        CancellationToken cancellationToken = default)
    {
        var orderResult = await GetOrderForAccessAsync(orderId, tenantId, clientId, cancellationToken);
        if (!orderResult.Succeeded || orderResult.Data == null)
        {
            return ServiceResult<MarketplaceShipmentLabelDownloadResult>.Failure(orderResult.Errors);
        }

        var order = orderResult.Data;
        var shipmentLookup = await BuildShipmentLookupAsync([order], cancellationToken);
        var shipments = GetOrderShipments(order, shipmentLookup);

        var resolvedShipmentId = !string.IsNullOrWhiteSpace(shipmentId)
            ? shipmentId.Trim()
            : shipments.Count switch
            {
                0 => NormalizeNullable(order.ShipmentId),
                1 => shipments[0].Shipment.ShipmentId,
                _ => NormalizeNullable(order.ShipmentId) ?? shipments[0].Shipment.ShipmentId
            };

        if (string.IsNullOrWhiteSpace(resolvedShipmentId))
        {
            return ServiceResult<MarketplaceShipmentLabelDownloadResult>.Failure([
                new ValidationError("shipmentId", "SHIPMENT_NOT_AVAILABLE")
            ]);
        }

        return await _labelService.GetOrFetchAsync(order.TenantId, order.ClientId, order.Provider, resolvedShipmentId, cancellationToken);
    }

    public async Task<ServiceResult<MarketplacePullShipmentLabelResult>> PullLabelAsync(
        Guid orderId,
        string? tenantId,
        Guid? clientId,
        string? shipmentId = null,
        CancellationToken cancellationToken = default)
    {
        var orderResult = await GetOrderForAccessAsync(orderId, tenantId, clientId, cancellationToken);
        if (!orderResult.Succeeded || orderResult.Data == null)
        {
            return ServiceResult<MarketplacePullShipmentLabelResult>.Failure(orderResult.Errors);
        }

        var order = orderResult.Data;
        var shipmentLookup = await BuildShipmentLookupAsync([order], cancellationToken);
        var shipments = GetOrderShipments(order, shipmentLookup);
        var resolvedShipmentId = !string.IsNullOrWhiteSpace(shipmentId)
            ? shipmentId.Trim()
            : ResolveDefaultShipmentId(order, shipments);

        if (string.IsNullOrWhiteSpace(resolvedShipmentId))
        {
            return ServiceResult<MarketplacePullShipmentLabelResult>.Failure([
                new ValidationError("shipmentId", "SHIPMENT_NOT_AVAILABLE")
            ]);
        }

        var shipment = shipments.FirstOrDefault(item => string.Equals(item.Shipment.ShipmentId, resolvedShipmentId, StringComparison.Ordinal))?.Shipment;
        if (shipment == null)
        {
            return ServiceResult<MarketplacePullShipmentLabelResult>.Failure([
                new ValidationError("shipmentId", "SHIPMENT_NOT_FOUND")
            ]);
        }

        var before = MarketplaceOrderWorkflow.ResolveLabelAvailability(shipment);
        var labelResult = await _labelService.GetOrFetchAsync(order.TenantId, order.ClientId, order.Provider, resolvedShipmentId, cancellationToken);
        if (!labelResult.Succeeded)
        {
            return ServiceResult<MarketplacePullShipmentLabelResult>.Success(new MarketplacePullShipmentLabelResult
            {
                OrderId = order.Id,
                ShipmentId = resolvedShipmentId,
                Succeeded = false,
                CachedNow = false,
                HasLabel = before != MarketplaceLabelAvailabilities.Pending,
                LabelAvailability = before,
                Message = labelResult.Errors.FirstOrDefault()?.Message ?? "Etiqueta ainda não foi liberada pelo canal."
            });
        }

        var refreshedShipment = await _dbContext.MarketplaceShipments
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.TenantId == order.TenantId
                                         && item.ClientId == order.ClientId
                                         && item.Provider == order.Provider
                                         && item.ShipmentId == resolvedShipmentId,
                cancellationToken);
        var availability = refreshedShipment == null
            ? MarketplaceLabelAvailabilities.Pending
            : MarketplaceOrderWorkflow.ResolveLabelAvailability(refreshedShipment);

        return ServiceResult<MarketplacePullShipmentLabelResult>.Success(new MarketplacePullShipmentLabelResult
        {
            OrderId = order.Id,
            ShipmentId = resolvedShipmentId,
            Succeeded = true,
            CachedNow = availability == MarketplaceLabelAvailabilities.AvailableCached,
            HasLabel = availability != MarketplaceLabelAvailabilities.Pending,
            LabelAvailability = availability,
            Message = availability == MarketplaceLabelAvailabilities.AvailableCached
                ? "Etiqueta puxada e cacheada com sucesso."
                : "Etiqueta localizada no canal."
        });
    }

    public async Task<ServiceResult<MarketplacePullLabelsBulkResult>> PullLabelsBulkAsync(
        string tenantId,
        Guid clientId,
        IReadOnlyCollection<Guid> orderIds,
        CancellationToken cancellationToken = default)
    {
        if (orderIds.Count == 0)
        {
            return ServiceResult<MarketplacePullLabelsBulkResult>.Success(new MarketplacePullLabelsBulkResult());
        }

        var items = new List<MarketplacePullShipmentLabelResult>();
        foreach (var orderId in orderIds.Distinct())
        {
            var orderResult = await GetOrderForAccessAsync(orderId, tenantId, clientId, cancellationToken);
            if (!orderResult.Succeeded || orderResult.Data == null)
            {
                items.Add(new MarketplacePullShipmentLabelResult
                {
                    OrderId = orderId,
                    Succeeded = false,
                    CachedNow = false,
                    HasLabel = false,
                    LabelAvailability = MarketplaceLabelAvailabilities.Pending,
                    Message = orderResult.Errors.FirstOrDefault()?.Message ?? "Pedido não encontrado."
                });
                continue;
            }

            var shipmentLookup = await BuildShipmentLookupAsync([orderResult.Data], cancellationToken);
            var shipments = GetOrderShipments(orderResult.Data, shipmentLookup);
            var shipmentIds = shipments.Select(item => item.Shipment.ShipmentId).Distinct(StringComparer.Ordinal).ToList();
            if (shipmentIds.Count == 0)
            {
                shipmentIds.Add(ResolveDefaultShipmentId(orderResult.Data, shipments) ?? string.Empty);
            }

            foreach (var shipmentId in shipmentIds.Where(id => !string.IsNullOrWhiteSpace(id)))
            {
                var result = await PullLabelAsync(orderId, tenantId, clientId, shipmentId, cancellationToken);
                if (result.Succeeded && result.Data != null)
                {
                    items.Add(result.Data);
                }
                else
                {
                    items.Add(new MarketplacePullShipmentLabelResult
                    {
                        OrderId = orderId,
                        ShipmentId = shipmentId,
                        Succeeded = false,
                        CachedNow = false,
                        HasLabel = false,
                        LabelAvailability = MarketplaceLabelAvailabilities.Pending,
                        Message = result.Errors.FirstOrDefault()?.Message ?? "Falha ao puxar etiqueta."
                    });
                }
            }
        }

        return ServiceResult<MarketplacePullLabelsBulkResult>.Success(new MarketplacePullLabelsBulkResult
        {
            Total = items.Count,
            Succeeded = items.Count(item => item.Succeeded),
            Failed = items.Count(item => !item.Succeeded),
            Items = items
        });
    }

    /// <summary>
    /// Avança um marco interno de expedição de um shipment.
    /// </summary>
    public async Task<ServiceResult<OrderActionResult>> AdvanceShipmentMilestoneAsync(
        Guid orderId,
        string shipmentId,
        string milestone,
        string advancedByAdminId,
        CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.MarketplaceOrders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order == null)
        {
            return ServiceResult<OrderActionResult>.Failure([
                new ValidationError("orderId", "ORDER_NOT_FOUND")
            ]);
        }

        if (string.IsNullOrWhiteSpace(shipmentId))
        {
            return ServiceResult<OrderActionResult>.Failure([
                new ValidationError("shipmentId", "SHIPMENT_NOT_AVAILABLE")
            ]);
        }

        var normalizedShipmentId = shipmentId.Trim();
        var shipment = await _dbContext.MarketplaceShipments.FirstOrDefaultAsync(
            item => item.TenantId == order.TenantId
                    && item.ClientId == order.ClientId
                    && item.Provider == order.Provider
                    && item.ShipmentId == normalizedShipmentId,
            cancellationToken);
        if (shipment == null && !string.Equals(order.ShipmentId, normalizedShipmentId, StringComparison.Ordinal))
        {
            return ServiceResult<OrderActionResult>.Failure([
                new ValidationError("shipmentId", "SHIPMENT_NOT_FOUND")
            ]);
        }

        var normalizedMilestone = NormalizeMilestone(milestone);
        if (normalizedMilestone == null)
        {
            return ServiceResult<OrderActionResult>.Failure([
                new ValidationError("milestone", "INVALID_MILESTONE")
            ]);
        }

        if (normalizedMilestone == MarketplaceShipmentMilestones.Dispatched)
        {
            return await MarkSingleShipmentDispatchedAsync(order, normalizedShipmentId, advancedByAdminId, cancellationToken);
        }

        var topic = GetMilestoneTopic(normalizedMilestone);
        if (topic == null)
        {
            return ServiceResult<OrderActionResult>.Failure([
                new ValidationError("milestone", "INVALID_MILESTONE")
            ]);
        }

        await _auditLogService.RecordAsync(
            order.TenantId,
            order.ClientId,
            order.Provider,
            order.SellerId,
            topic,
            normalizedShipmentId,
            new
            {
                orderId = order.MlOrderId,
                shipmentId = normalizedShipmentId,
                milestone = normalizedMilestone,
                advancedByAdminId
            },
            "v1",
            cancellationToken);

        order.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<OrderActionResult>.Success(new OrderActionResult
        {
            OrderId = order.Id,
            Status = order.Status,
            Action = normalizedMilestone,
            UpdatedAt = order.UpdatedAt
        });
    }

    /// <summary>
    /// Marca o pedido como despachado.
    /// </summary>
    public async Task<ServiceResult<OrderActionResult>> MarkDispatchedAsync(
        Guid orderId,
        string dispatchedByAdminId,
        CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.MarketplaceOrders
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

        if (order == null)
            return ServiceResult<OrderActionResult>.Failure([new ValidationError("orderId", "ORDER_NOT_FOUND")]);

        if (!order.SabrPaymentConfirmedAt.HasValue)
            return ServiceResult<OrderActionResult>.Failure([new ValidationError("status", $"ORDER_NOT_DISPATCHABLE: {order.Status}")]);

        var nowUtc = DateTimeOffset.UtcNow;
        order.UpdatedAt = nowUtc;

        var shipmentIds = await GetConcreteShipmentIdsAsync(order, cancellationToken);
        foreach (var shipmentId in shipmentIds)
        {
            await _auditLogService.RecordAsync(
                order.TenantId,
                order.ClientId,
                order.Provider,
                order.SellerId,
                MarketplaceEventTopics.AuditFulfillmentDispatched,
                shipmentId,
                new { orderId = order.MlOrderId, shipmentId, dispatchedByAdminId },
                "v1",
                cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditLogService.RecordAsync(
            order.TenantId,
            order.ClientId,
            order.Provider,
            order.SellerId,
            MarketplaceEventTopics.AuditOrderDispatched,
            order.MlOrderId,
            new { orderId = order.MlOrderId, dispatchedByAdminId },
            "v1",
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (order.Provider == MarketplaceProvider.TinyErp && !string.IsNullOrWhiteSpace(order.ShipmentId))
        {
            _ = _tinyIntegrationService.UpdateOrderDispatchAsync(order.Id, order.ShipmentId, string.Empty);
        }

        return ServiceResult<OrderActionResult>.Success(new OrderActionResult
        {
            OrderId   = order.Id,
            Status    = order.Status,
            Action    = MarketplaceShipmentMilestones.Dispatched,
            UpdatedAt = order.UpdatedAt
        });
    }

    /// <summary>
    /// Lista pedidos prontos para expedição (status = payment_confirmed ou label_generated).
    /// </summary>
    public async Task<PagedResult<AdminFulfillmentOrderResult>> ListFulfillmentAsync(
        int skip,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTimeOffset.UtcNow;

        var query = _dbContext.MarketplaceOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.SabrPaymentConfirmedAt.HasValue
                        && o.Status != MarketplaceOrderStatuses.Cancelled
                        && o.Status != MarketplaceOrderStatuses.Refunded
                        && o.Status != MarketplaceOrderStatuses.Delivered)
            .OrderBy(o => o.ShipByDeadlineAt.HasValue ? 0 : 1)
            .ThenBy(o => o.ShipByDeadlineAt)
            .ThenBy(o => o.SabrPaymentConfirmedAt);

        var orders = await query.ToListAsync(cancellationToken);

        var clientIds = orders.Select(o => o.ClientId).Distinct().ToList();
        var clients = await _dbContext.Clients
            .AsNoTracking()
            .Where(c => clientIds.Contains(c.Id))
            .Select(c => new { c.Id, c.AccountName })
            .ToDictionaryAsync(c => c.Id, c => c.AccountName, cancellationToken);

        var shipmentLookup = await BuildShipmentLookupAsync(orders, cancellationToken);

        var allItems = orders
            .Select(order => BuildOrderView(order, GetOrderShipments(order, shipmentLookup)))
            .Where(view => view.CurrentInternalStage != MarketplaceInternalStages.Dispatched)
            .OrderBy(view => view.Order.ShipByDeadlineAt.HasValue ? 0 : 1)
            .ThenBy(view => view.Order.ShipByDeadlineAt)
            .ThenBy(view => view.Order.SabrPaymentConfirmedAt)
            .Select(view => new AdminFulfillmentOrderResult
            {
                Id = view.Order.Id,
                TenantId = view.Order.TenantId,
                ClientId = view.Order.ClientId,
                ClientName = clients.GetValueOrDefault(view.Order.ClientId),
                MlOrderId = view.Order.MlOrderId,
                SellerId = view.Order.SellerId.ToString(),
                ShipmentId = view.PrimaryShipment?.Shipment.ShipmentId ?? view.Order.ShipmentId,
                ShippingMode = view.PrimaryShipment?.Shipment.ShippingMode ?? view.Order.ShippingMode,
                LogisticType = view.PrimaryShipment?.Shipment.LogisticType ?? view.Order.LogisticType,
                ShipByDeadlineAt = view.PrimaryShipment?.Shipment.ShipByDeadlineAt ?? view.Order.ShipByDeadlineAt,
                IsUrgent = (view.PrimaryShipment?.Shipment.ShipByDeadlineAt ?? view.Order.ShipByDeadlineAt) is { } deadline
                           && deadline <= nowUtc.AddHours(4),
                HasLabel = view.HasLabel,
                LabelAvailability = view.LabelAvailability,
                TotalItems = view.Order.Items.Count,
                SabrPaymentConfirmedAt = view.Order.SabrPaymentConfirmedAt ?? view.Order.CreatedAt,
                Shipments = view.Shipments.Select(MapShipmentResult).ToList(),
                ChannelStatus = view.ChannelStatus,
                CancellationRequest = view.CancellationRequest,
                InternalFulfillmentSummary = view.InternalSummary
            })
            .ToList();

        var total = allItems.Count;
        var items = allItems.Skip(skip).Take(limit).ToList();

        return new PagedResult<AdminFulfillmentOrderResult>
        {
            Items = items,
            Total = total,
            Skip  = skip,
            Limit = limit
        };
    }

    private async Task<ServiceResult<MarketplaceOrder>> GetOrderForAccessAsync(
        Guid orderId,
        string? tenantId,
        Guid? clientId,
        CancellationToken cancellationToken)
    {
        var query = _dbContext.MarketplaceOrders.AsNoTracking().Where(o => o.Id == orderId);
        if (!string.IsNullOrWhiteSpace(tenantId))
            query = query.Where(o => o.TenantId == tenantId);
        if (clientId.HasValue && clientId != Guid.Empty)
            query = query.Where(o => o.ClientId == clientId.Value);

        var order = await query.FirstOrDefaultAsync(cancellationToken);
        if (order == null)
        {
            return ServiceResult<MarketplaceOrder>.Failure([
                new ValidationError("orderId", "ORDER_NOT_FOUND")
            ]);
        }

        return ServiceResult<MarketplaceOrder>.Success(order);
    }

    private async Task<Dictionary<string, List<OrderShipmentSnapshot>>> BuildShipmentLookupAsync(
        IReadOnlyCollection<MarketplaceOrder> orders,
        CancellationToken cancellationToken)
    {
        if (orders.Count == 0)
        {
            return new Dictionary<string, List<OrderShipmentSnapshot>>(StringComparer.Ordinal);
        }

        var orderIds = orders.Select(order => order.MlOrderId).Distinct(StringComparer.Ordinal).ToList();
        var shipments = await _dbContext.MarketplaceShipments
            .AsNoTracking()
            .Where(item => item.MlOrderId != null && orderIds.Contains(item.MlOrderId))
            .ToListAsync(cancellationToken);

        var milestonesLookup = await BuildShipmentMilestonesLookupAsync(shipments, cancellationToken);
        var grouped = new Dictionary<string, List<OrderShipmentSnapshot>>(StringComparer.Ordinal);

        foreach (var order in orders)
        {
            var orderKey = BuildOrderKey(order.TenantId, order.ClientId, order.Provider, order.MlOrderId);
            var shipmentList = shipments
                .Where(item => string.Equals(item.TenantId, order.TenantId, StringComparison.Ordinal)
                               && item.ClientId == order.ClientId
                               && item.Provider == order.Provider
                               && string.Equals(item.MlOrderId, order.MlOrderId, StringComparison.Ordinal))
                .OrderBy(item => item.CreatedAt)
                .ToList();

            if (shipmentList.Count == 0 && !string.IsNullOrWhiteSpace(order.ShipmentId))
            {
                shipmentList.Add(new MarketplaceShipment
                {
                    TenantId = order.TenantId,
                    ClientId = order.ClientId,
                    Provider = order.Provider,
                    SellerId = order.SellerId,
                    ShipmentId = order.ShipmentId!,
                    MlOrderId = order.MlOrderId,
                    Status = order.Status,
                    ShippingMode = order.ShippingMode,
                    LogisticType = order.LogisticType,
                    ShipByDeadlineAt = order.ShipByDeadlineAt,
                    CreatedAt = order.CreatedAt,
                    UpdatedAt = order.UpdatedAt
                });
            }

            grouped[orderKey] = shipmentList
                .Select(shipment =>
                {
                    var shipmentKey = BuildShipmentKey(order.TenantId, order.ClientId, order.Provider, shipment.ShipmentId);
                    milestonesLookup.TryGetValue(shipmentKey, out var milestoneResult);
                    return new OrderShipmentSnapshot(
                        shipment,
                        milestoneResult ?? new MarketplaceShipmentMilestonesResult());
                })
                .ToList();
        }

        return grouped;
    }

    private async Task<Dictionary<string, MarketplaceShipmentMilestonesResult>> BuildShipmentMilestonesLookupAsync(
        IReadOnlyCollection<MarketplaceShipment> shipments,
        CancellationToken cancellationToken)
    {
        if (shipments.Count == 0)
        {
            return new Dictionary<string, MarketplaceShipmentMilestonesResult>(StringComparer.Ordinal);
        }

        var shipmentIds = shipments.Select(item => item.ShipmentId).Distinct(StringComparer.Ordinal).ToList();
        var tenantIds = shipments.Select(item => item.TenantId).Distinct(StringComparer.Ordinal).ToList();
        var clientIds = shipments.Select(item => item.ClientId).Distinct().ToList();
        var providers = shipments.Select(item => item.Provider).Distinct().ToList();

        var logs = await _dbContext.MarketplaceEventLogs
            .AsNoTracking()
            .Where(item => shipmentIds.Contains(item.ResourceId)
                           && tenantIds.Contains(item.TenantId)
                           && clientIds.Contains(item.ClientId)
                           && providers.Contains(item.Provider)
                           && (item.Topic == MarketplaceEventTopics.AuditFulfillmentProcessingStarted
                               || item.Topic == MarketplaceEventTopics.AuditFulfillmentLabelPrinted
                               || item.Topic == MarketplaceEventTopics.AuditFulfillmentSeparated
                               || item.Topic == MarketplaceEventTopics.AuditFulfillmentDispatched))
            .ToListAsync(cancellationToken);

        var result = new Dictionary<string, MarketplaceShipmentMilestonesResult>(StringComparer.Ordinal);
        foreach (var shipment in shipments)
        {
            var key = BuildShipmentKey(shipment.TenantId, shipment.ClientId, shipment.Provider, shipment.ShipmentId);
            var shipmentLogs = logs.Where(item =>
                string.Equals(item.TenantId, shipment.TenantId, StringComparison.Ordinal)
                && item.ClientId == shipment.ClientId
                && item.Provider == shipment.Provider
                && string.Equals(item.ResourceId, shipment.ShipmentId, StringComparison.Ordinal));

            result[key] = new MarketplaceShipmentMilestonesResult
            {
                ProcessingStartedAt = shipmentLogs
                    .Where(item => item.Topic == MarketplaceEventTopics.AuditFulfillmentProcessingStarted)
                    .Select(item => (DateTimeOffset?)item.CreatedAt)
                    .OrderByDescending(item => item)
                    .FirstOrDefault(),
                LabelPrintedAt = shipmentLogs
                    .Where(item => item.Topic == MarketplaceEventTopics.AuditFulfillmentLabelPrinted)
                    .Select(item => (DateTimeOffset?)item.CreatedAt)
                    .OrderByDescending(item => item)
                    .FirstOrDefault(),
                SeparatedAt = shipmentLogs
                    .Where(item => item.Topic == MarketplaceEventTopics.AuditFulfillmentSeparated)
                    .Select(item => (DateTimeOffset?)item.CreatedAt)
                    .OrderByDescending(item => item)
                    .FirstOrDefault(),
                DispatchedAt = shipmentLogs
                    .Where(item => item.Topic == MarketplaceEventTopics.AuditFulfillmentDispatched)
                    .Select(item => (DateTimeOffset?)item.CreatedAt)
                    .OrderByDescending(item => item)
                    .FirstOrDefault()
            };
        }

        return result;
    }

    private static List<OrderShipmentSnapshot> GetOrderShipments(
        MarketplaceOrder order,
        IReadOnlyDictionary<string, List<OrderShipmentSnapshot>> shipmentLookup)
    {
        var key = BuildOrderKey(order.TenantId, order.ClientId, order.Provider, order.MlOrderId);
        return shipmentLookup.TryGetValue(key, out var shipments)
            ? shipments
            : [];
    }

    private static MarketplaceShipmentResult MapShipmentResult(OrderShipmentSnapshot shipment)
    {
        return new MarketplaceShipmentResult
        {
            ShipmentId = shipment.Shipment.ShipmentId,
            Status = shipment.Shipment.Status,
            Substatus = shipment.Shipment.Substatus,
            ShippingMode = shipment.Shipment.ShippingMode,
            LogisticType = shipment.Shipment.LogisticType,
            TrackingNumber = shipment.Shipment.TrackingNumber,
            TrackingMethod = shipment.Shipment.TrackingMethod,
            TrackingUrl = shipment.Shipment.TrackingUrl,
            ShippingProvider = ResolveShippingProvider(shipment.Shipment),
            ShippedAt = shipment.Shipment.ShippedAt,
            ShipByDeadlineAt = shipment.Shipment.ShipByDeadlineAt,
            HasLabel = MarketplaceOrderWorkflow.HasOperationalLabel(shipment.Shipment),
            LabelAvailability = MarketplaceOrderWorkflow.ResolveLabelAvailability(shipment.Shipment),
            Milestones = shipment.Milestones
        };
    }

    private static MarketplaceInternalFulfillmentSummaryResult BuildInternalSummary(MarketplaceOrder order, IReadOnlyCollection<OrderShipmentSnapshot> shipments)
    {
        var milestones = new MarketplaceShipmentMilestonesResult
        {
            ReceivedAt = order.ImportedAt,
            PaidAt = order.SabrPaymentConfirmedAt,
            ProcessingStartedAt = GetLatest(shipment => shipment.Milestones.ProcessingStartedAt, shipments) ?? order.SabrPaymentConfirmedAt,
            LabelPrintedAt = GetLatest(shipment => shipment.Milestones.LabelPrintedAt, shipments),
            SeparatedAt = GetLatest(shipment => shipment.Milestones.SeparatedAt, shipments),
            DispatchedAt = GetLatest(shipment => shipment.Milestones.DispatchedAt, shipments)
        };

        var stage = milestones.DispatchedAt.HasValue
            ? MarketplaceInternalStages.Dispatched
            : milestones.SeparatedAt.HasValue
                ? MarketplaceInternalStages.Separated
                : milestones.LabelPrintedAt.HasValue
                    ? MarketplaceInternalStages.LabelPrinted
                    : milestones.ProcessingStartedAt.HasValue
                        ? MarketplaceInternalStages.ProcessingStarted
                        : milestones.PaidAt.HasValue
                            ? MarketplaceInternalStages.Paid
                            : milestones.ReceivedAt.HasValue
                                ? MarketplaceInternalStages.Received
                                : MarketplaceInternalStages.Pending;

        return new MarketplaceInternalFulfillmentSummaryResult
        {
            Stage = stage,
            Label = stage switch
            {
                MarketplaceInternalStages.Dispatched => "Pedido enviado",
                MarketplaceInternalStages.Separated => "Pedido separado",
                MarketplaceInternalStages.LabelPrinted => "Etiqueta impressa",
                MarketplaceInternalStages.ProcessingStarted => "Em processamento",
                MarketplaceInternalStages.Paid => "Pedido pago",
                MarketplaceInternalStages.Received => "Pedido recebido",
                _ => "Aguardando processamento"
            },
            Milestones = milestones
        };
    }

    private async Task<List<string>> GetConcreteShipmentIdsAsync(MarketplaceOrder order, CancellationToken cancellationToken)
    {
        var shipmentIds = await _dbContext.MarketplaceShipments
            .AsNoTracking()
            .Where(item => item.TenantId == order.TenantId
                           && item.ClientId == order.ClientId
                           && item.Provider == order.Provider
                           && item.MlOrderId == order.MlOrderId)
            .Select(item => item.ShipmentId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (shipmentIds.Count == 0 && !string.IsNullOrWhiteSpace(order.ShipmentId))
        {
            shipmentIds.Add(order.ShipmentId);
        }

        return shipmentIds;
    }

    private async Task<ServiceResult<OrderActionResult>> MarkSingleShipmentDispatchedAsync(
        MarketplaceOrder order,
        string shipmentId,
        string dispatchedByAdminId,
        CancellationToken cancellationToken)
    {
        if (!order.SabrPaymentConfirmedAt.HasValue)
        {
            return ServiceResult<OrderActionResult>.Failure([
                new ValidationError("status", $"ORDER_NOT_DISPATCHABLE: {order.Status}")
            ]);
        }

        await _auditLogService.RecordAsync(
            order.TenantId,
            order.ClientId,
            order.Provider,
            order.SellerId,
            MarketplaceEventTopics.AuditFulfillmentDispatched,
            shipmentId,
            new { orderId = order.MlOrderId, shipmentId, dispatchedByAdminId },
            "v1",
            cancellationToken);

        order.UpdatedAt = DateTimeOffset.UtcNow;

        await _auditLogService.RecordAsync(
            order.TenantId,
            order.ClientId,
            order.Provider,
            order.SellerId,
            MarketplaceEventTopics.AuditOrderDispatched,
            order.MlOrderId,
            new { orderId = order.MlOrderId, dispatchedByAdminId },
            "v1",
            cancellationToken);

        if (order.Provider == MarketplaceProvider.TinyErp && !string.IsNullOrWhiteSpace(order.ShipmentId))
        {
            _ = _tinyIntegrationService.UpdateOrderDispatchAsync(order.Id, order.ShipmentId, string.Empty);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<OrderActionResult>.Success(new OrderActionResult
        {
            OrderId = order.Id,
            Status = order.Status,
            Action = MarketplaceShipmentMilestones.Dispatched,
            UpdatedAt = order.UpdatedAt
        });
    }

    private static OrderView BuildOrderView(MarketplaceOrder order, IReadOnlyCollection<OrderShipmentSnapshot> shipments)
    {
        var primaryShipment = FindPrimaryShipment(shipments);
        var internalSummary = BuildInternalSummary(order, shipments);
        var channelStatus = BuildChannelStatus(order, shipments, primaryShipment);
        var labelAvailability = ResolveOrderLabelAvailability(shipments);
        var requiresLabelForPayment = MarketplaceOrderWorkflow.RequiresLabelForPayment(order.Provider);
        var canMarkPaid = !requiresLabelForPayment || shipments.Any(item => MarketplaceOrderWorkflow.HasOperationalLabel(item.Shipment));

        return new OrderView(
            order,
            shipments,
            primaryShipment,
            internalSummary,
            channelStatus,
            MarketplaceOrderWorkflow.BuildCancellationRequest(order),
            labelAvailability,
            labelAvailability != MarketplaceLabelAvailabilities.Pending,
            internalSummary.Stage,
            channelStatus.Stage,
            requiresLabelForPayment,
            canMarkPaid);
    }

    private static MarketplaceOrderListItemResult MapClientOrderListItem(OrderView view)
    {
        return new MarketplaceOrderListItemResult
        {
            Id = view.Order.Id,
            Provider = view.Order.Provider,
            SellerId = view.Order.SellerId.ToString(),
            MlOrderId = view.Order.MlOrderId,
            Status = view.Order.Status,
            PaidAt = view.Order.PaidAt,
            SabrPaymentConfirmedAt = view.Order.SabrPaymentConfirmedAt,
            ShippingMode = view.PrimaryShipment?.Shipment.ShippingMode ?? view.Order.ShippingMode,
            LogisticType = view.PrimaryShipment?.Shipment.LogisticType ?? view.Order.LogisticType,
            ShipByDeadlineAt = view.PrimaryShipment?.Shipment.ShipByDeadlineAt ?? view.Order.ShipByDeadlineAt,
            HasUnmappedItems = view.Order.Items.Any(item => item.MappingState == MarketplaceMappingStates.Unmapped),
            TotalItems = view.Order.Items.Count,
            ReservedItems = view.Order.Items.Sum(item => item.ReservedQuantity),
            HasLabel = view.HasLabel,
            LabelAvailability = view.LabelAvailability,
            RequiresLabelForPayment = view.RequiresLabelForPayment,
            CanMarkPaid = view.CanMarkPaid,
            ShipmentsCount = view.Shipments.Count,
            TrackingNumber = view.PrimaryShipment?.Shipment.TrackingNumber,
            TrackingUrl = view.PrimaryShipment?.Shipment.TrackingUrl,
            ShippingProvider = ResolveShippingProvider(view.PrimaryShipment?.Shipment),
            CurrentInternalStage = view.CurrentInternalStage,
            CurrentChannelStage = view.CurrentChannelStage,
            ChannelStatus = view.ChannelStatus,
            CancellationRequest = view.CancellationRequest,
            InternalFulfillmentSummary = view.InternalSummary,
            RiskFlagsJson = view.Order.RiskFlagsJson,
            ImportedAt = view.Order.ImportedAt
        };
    }

    private static MarketplaceOrderDetailResult MapClientOrderDetail(
        OrderView view,
        List<MarketplaceShipmentResult> shipments,
        List<MarketplaceOrderItemDetailResult> items)
    {
        return new MarketplaceOrderDetailResult
        {
            Id = view.Order.Id,
            Provider = view.Order.Provider,
            SellerId = view.Order.SellerId.ToString(),
            MlOrderId = view.Order.MlOrderId,
            Status = view.Order.Status,
            PaidAt = view.Order.PaidAt,
            SabrPaymentConfirmedAt = view.Order.SabrPaymentConfirmedAt,
            ShipmentId = view.PrimaryShipment?.Shipment.ShipmentId ?? view.Order.ShipmentId,
            ShippingMode = view.PrimaryShipment?.Shipment.ShippingMode ?? view.Order.ShippingMode,
            LogisticType = view.PrimaryShipment?.Shipment.LogisticType ?? view.Order.LogisticType,
            ShipByDeadlineAt = view.PrimaryShipment?.Shipment.ShipByDeadlineAt ?? view.Order.ShipByDeadlineAt,
            ImportedAt = view.Order.ImportedAt,
            CanCancel = MarketplaceOrderStatuses.CancellableStatuses.Contains(view.Order.Status),
            CanRefund = MarketplaceOrderStatuses.RefundableStatuses.Contains(view.Order.Status),
            RequiresLabelForPayment = view.RequiresLabelForPayment,
            CanMarkPaid = view.CanMarkPaid,
            CanAutoCancel = MarketplaceOrderWorkflow.CanAutoCancel(view.CurrentInternalStage),
            CurrentInternalStage = view.CurrentInternalStage,
            CurrentChannelStage = view.CurrentChannelStage,
            ChannelStatus = view.ChannelStatus,
            CancellationRequest = view.CancellationRequest,
            Items = items,
            Shipments = shipments,
            InternalFulfillmentSummary = view.InternalSummary
        };
    }

    private static AdminOrderListItemResult MapAdminOrderListItem(OrderView view, string? clientName)
    {
        return new AdminOrderListItemResult
        {
            Id = view.Order.Id,
            TenantId = view.Order.TenantId,
            ClientId = view.Order.ClientId,
            ClientName = clientName,
            Provider = view.Order.Provider,
            SellerId = view.Order.SellerId.ToString(),
            MlOrderId = view.Order.MlOrderId,
            Status = view.Order.Status,
            PaidAt = view.Order.PaidAt,
            SabrPaymentConfirmedAt = view.Order.SabrPaymentConfirmedAt,
            ShipmentId = view.PrimaryShipment?.Shipment.ShipmentId ?? view.Order.ShipmentId,
            ShippingMode = view.PrimaryShipment?.Shipment.ShippingMode ?? view.Order.ShippingMode,
            LogisticType = view.PrimaryShipment?.Shipment.LogisticType ?? view.Order.LogisticType,
            ShipByDeadlineAt = view.PrimaryShipment?.Shipment.ShipByDeadlineAt ?? view.Order.ShipByDeadlineAt,
            HasUnmappedItems = view.Order.Items.Any(i => i.MappingState == MarketplaceMappingStates.Unmapped),
            TotalItems = view.Order.Items.Count,
            HasLabel = view.HasLabel,
            LabelAvailability = view.LabelAvailability,
            RequiresLabelForPayment = view.RequiresLabelForPayment,
            CanMarkPaid = view.CanMarkPaid,
            CurrentInternalStage = view.CurrentInternalStage,
            ChannelStatus = view.ChannelStatus,
            CancellationRequest = view.CancellationRequest,
            InternalFulfillmentSummary = view.InternalSummary,
            RiskFlagsJson = view.Order.RiskFlagsJson,
            ImportedAt = view.Order.ImportedAt
        };
    }

    private static MarketplaceChannelStatusResult BuildChannelStatus(
        MarketplaceOrder order,
        IReadOnlyCollection<OrderShipmentSnapshot> shipments,
        OrderShipmentSnapshot? primaryShipment)
    {
        var shipmentDispatched = shipments
            .Where(item => MarketplaceOrderWorkflow.IsExternalDispatched(item.Shipment))
            .Select(item => item.Shipment.ShippedAt ?? item.Shipment.UpdatedAt)
            .OrderByDescending(item => item)
            .FirstOrDefault();

        var rawStatus = primaryShipment?.Shipment.Status ?? order.Status;
        var stage = shipmentDispatched != default
            ? MarketplaceChannelStages.Dispatched
            : order.Status switch
            {
                MarketplaceOrderStatuses.Delivered => MarketplaceChannelStages.Delivered,
                MarketplaceOrderStatuses.Cancelled => MarketplaceChannelStages.Cancelled,
                MarketplaceOrderStatuses.RefundRequested => MarketplaceChannelStages.RefundRequested,
                MarketplaceOrderStatuses.Refunded => MarketplaceChannelStages.Refunded,
                _ when MarketplaceOrderWorkflow.IsExternalDelivered(rawStatus) => MarketplaceChannelStages.Delivered,
                _ when MarketplaceOrderWorkflow.IsExternalCancelled(rawStatus) => MarketplaceChannelStages.Cancelled,
                _ => MarketplaceChannelStages.AwaitingShipment
            };

        return new MarketplaceChannelStatusResult
        {
            Stage = stage,
            Label = MarketplaceOrderWorkflow.ToChannelLabel(stage, rawStatus),
            OccurredAt = stage == MarketplaceChannelStages.Dispatched
                ? shipmentDispatched
                : primaryShipment?.Shipment.ShippedAt,
            RawStatus = rawStatus
        };
    }

    private static OrderShipmentSnapshot? FindPrimaryShipment(IReadOnlyCollection<OrderShipmentSnapshot> shipments)
    {
        return shipments
            .OrderByDescending(item => MarketplaceOrderWorkflow.ResolveLabelAvailability(item.Shipment) == MarketplaceLabelAvailabilities.AvailableCached)
            .ThenByDescending(item => MarketplaceOrderWorkflow.ResolveLabelAvailability(item.Shipment) == MarketplaceLabelAvailabilities.AvailableRemote)
            .ThenByDescending(item => !string.IsNullOrWhiteSpace(item.Shipment.TrackingNumber))
            .ThenBy(item => item.Shipment.CreatedAt)
            .FirstOrDefault();
    }

    private static string ResolveOrderLabelAvailability(IReadOnlyCollection<OrderShipmentSnapshot> shipments)
    {
        if (shipments.Any(item => MarketplaceOrderWorkflow.ResolveLabelAvailability(item.Shipment) == MarketplaceLabelAvailabilities.AvailableCached))
        {
            return MarketplaceLabelAvailabilities.AvailableCached;
        }

        if (shipments.Any(item => MarketplaceOrderWorkflow.ResolveLabelAvailability(item.Shipment) == MarketplaceLabelAvailabilities.AvailableRemote))
        {
            return MarketplaceLabelAvailabilities.AvailableRemote;
        }

        return MarketplaceLabelAvailabilities.Pending;
    }

    private static DateTimeOffset? GetLatest(
        Func<OrderShipmentSnapshot, DateTimeOffset?> selector,
        IReadOnlyCollection<OrderShipmentSnapshot> shipments)
    {
        var values = shipments
            .Select(selector)
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .OrderByDescending(item => item)
            .ToList();

        return values.Count == 0 ? null : values[0];
    }

    private static bool MatchesInternalStatus(OrderView view, string? filter)
        => string.IsNullOrWhiteSpace(filter)
           || string.Equals(view.CurrentInternalStage, filter, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesChannelStatus(OrderView view, string? filter)
        => string.IsNullOrWhiteSpace(filter)
           || string.Equals(view.CurrentChannelStage, filter, StringComparison.OrdinalIgnoreCase)
           || string.Equals(view.Order.Status, filter, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeFilter(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string? ResolveDefaultShipmentId(MarketplaceOrder order, IReadOnlyCollection<OrderShipmentSnapshot> shipments)
        => !string.IsNullOrWhiteSpace(order.ShipmentId)
            ? order.ShipmentId.Trim()
            : shipments.FirstOrDefault()?.Shipment.ShipmentId;

    private static string BuildOrderKey(string tenantId, Guid clientId, MarketplaceProvider provider, string orderId)
        => $"{tenantId}|{clientId:N}|{(int)provider}|{orderId}";

    private static string BuildShipmentKey(string tenantId, Guid clientId, MarketplaceProvider provider, string shipmentId)
        => $"{tenantId}|{clientId:N}|{(int)provider}|{shipmentId}";

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeMilestone(string? milestone)
        => milestone?.Trim().ToLowerInvariant() switch
        {
            MarketplaceShipmentMilestones.ProcessingStarted => MarketplaceShipmentMilestones.ProcessingStarted,
            MarketplaceShipmentMilestones.LabelPrinted => MarketplaceShipmentMilestones.LabelPrinted,
            MarketplaceShipmentMilestones.Separated => MarketplaceShipmentMilestones.Separated,
            MarketplaceShipmentMilestones.Dispatched => MarketplaceShipmentMilestones.Dispatched,
            _ => null
        };

    private static string? GetMilestoneTopic(string? milestone)
        => milestone switch
        {
            MarketplaceShipmentMilestones.ProcessingStarted => MarketplaceEventTopics.AuditFulfillmentProcessingStarted,
            MarketplaceShipmentMilestones.LabelPrinted => MarketplaceEventTopics.AuditFulfillmentLabelPrinted,
            MarketplaceShipmentMilestones.Separated => MarketplaceEventTopics.AuditFulfillmentSeparated,
            MarketplaceShipmentMilestones.Dispatched => MarketplaceEventTopics.AuditFulfillmentDispatched,
            _ => null
        };

    private static string? ResolveShippingProvider(MarketplaceShipment? shipment)
    {
        if (shipment == null)
        {
            return null;
        }

        return NormalizeNullable(shipment.TrackingMethod)
               ?? NormalizeNullable(shipment.LogisticType)
               ?? NormalizeNullable(shipment.ShippingMode)
               ?? NormalizeNullable(shipment.Status);
    }

    private sealed record OrderShipmentSnapshot(
        MarketplaceShipment Shipment,
        MarketplaceShipmentMilestonesResult Milestones);

    private sealed record OrderView(
        MarketplaceOrder Order,
        IReadOnlyCollection<OrderShipmentSnapshot> Shipments,
        OrderShipmentSnapshot? PrimaryShipment,
        MarketplaceInternalFulfillmentSummaryResult InternalSummary,
        MarketplaceChannelStatusResult ChannelStatus,
        MarketplaceCancellationRequestResult CancellationRequest,
        string LabelAvailability,
        bool HasLabel,
        string CurrentInternalStage,
        string CurrentChannelStage,
        bool RequiresLabelForPayment,
        bool CanMarkPaid);
}
