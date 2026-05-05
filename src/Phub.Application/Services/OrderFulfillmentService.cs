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
        string? status,
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

        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalizedStatus = status.Trim().ToLowerInvariant();
            query = query.Where(o => o.Status.ToLower() == normalizedStatus);
        }

        if (!string.IsNullOrWhiteSpace(logisticType))
        {
            var normalizedLogisticType = logisticType.Trim().ToLowerInvariant();
            query = query.Where(o => o.LogisticType != null && o.LogisticType.ToLower() == normalizedLogisticType);
        }

        var total = await query.CountAsync(cancellationToken);
        var orders = await query
            .OrderByDescending(o => o.ImportedAt)
            .Skip(safeSkip)
            .Take(safeLimit)
            .ToListAsync(cancellationToken);

        var shipmentLookup = await BuildShipmentLookupAsync(orders, cancellationToken);

        var items = orders.Select(order =>
        {
            var shipments = GetOrderShipments(order, shipmentLookup);
            var primaryShipment = shipments
                .OrderByDescending(item => item.Shipment.LabelContentBytes != null && item.Shipment.LabelContentBytes.Length > 0)
                .ThenByDescending(item => !string.IsNullOrWhiteSpace(item.Shipment.TrackingNumber))
                .ThenBy(item => item.Shipment.CreatedAt)
                .FirstOrDefault();
            var internalSummary = BuildInternalSummary(shipments);

            return new MarketplaceOrderListItemResult
            {
                Id = order.Id,
                Provider = order.Provider,
                SellerId = order.SellerId.ToString(),
                MlOrderId = order.MlOrderId,
                Status = order.Status,
                PaidAt = order.PaidAt,
                SabrPaymentConfirmedAt = order.SabrPaymentConfirmedAt,
                ShippingMode = primaryShipment?.Shipment.ShippingMode ?? order.ShippingMode,
                LogisticType = primaryShipment?.Shipment.LogisticType ?? order.LogisticType,
                ShipByDeadlineAt = primaryShipment?.Shipment.ShipByDeadlineAt ?? order.ShipByDeadlineAt,
                HasUnmappedItems = order.Items.Any(item => item.MappingState == MarketplaceMappingStates.Unmapped),
                TotalItems = order.Items.Count,
                ReservedItems = order.Items.Sum(item => item.ReservedQuantity),
                HasLabel = shipments.Any(item => item.Shipment.LabelContentBytes != null && item.Shipment.LabelContentBytes.Length > 0),
                ShipmentsCount = shipments.Count,
                TrackingNumber = primaryShipment?.Shipment.TrackingNumber,
                TrackingUrl = primaryShipment?.Shipment.TrackingUrl,
                ShippingProvider = ResolveShippingProvider(primaryShipment?.Shipment),
                InternalFulfillmentSummary = internalSummary,
                RiskFlagsJson = order.RiskFlagsJson,
                ImportedAt = order.ImportedAt
            };
        }).ToList();

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

        var result = new MarketplaceOrderDetailResult
        {
            Id = order.Id,
            Provider = order.Provider,
            SellerId = order.SellerId.ToString(),
            MlOrderId = order.MlOrderId,
            Status = order.Status,
            PaidAt = order.PaidAt,
            SabrPaymentConfirmedAt = order.SabrPaymentConfirmedAt,
            ShipmentId = order.ShipmentId,
            ShippingMode = shipments.FirstOrDefault()?.ShippingMode ?? order.ShippingMode,
            LogisticType = shipments.FirstOrDefault()?.LogisticType ?? order.LogisticType,
            ShipByDeadlineAt = shipments.FirstOrDefault()?.ShipByDeadlineAt ?? order.ShipByDeadlineAt,
            ImportedAt = order.ImportedAt,
            CanCancel = MarketplaceOrderStatuses.CancellableStatuses.Contains(order.Status),
            CanRefund = MarketplaceOrderStatuses.RefundableStatuses.Contains(order.Status),
            Items = order.Items.Select(i => new MarketplaceOrderItemDetailResult
            {
                Id = i.Id,
                MlItemId = i.MlItemId,
                MlVariationId = i.MlVariationId,
                SabrVariantSku = i.SabrVariantSku,
                ProductName = i.SabrVariantSku != null ? products.GetValueOrDefault(i.SabrVariantSku) : null,
                Quantity = i.Quantity,
                MappingState = i.MappingState
            }).ToList(),
            Shipments = shipments,
            InternalFulfillmentSummary = BuildInternalSummary(GetOrderShipments(order, shipmentLookup))
        };

        return ServiceResult<MarketplaceOrderDetailResult>.Success(result);
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
                HasLabel = item.Shipment.LabelContentBytes != null && item.Shipment.LabelContentBytes.Length > 0,
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

        if (normalizedMilestone == MarketplaceShipmentMilestones.LabelPrinted
            && string.Equals(order.Status, MarketplaceOrderStatuses.PaymentConfirmed, StringComparison.Ordinal))
        {
            order.Status = MarketplaceOrderStatuses.LabelGenerated;
            order.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<OrderActionResult>.Success(new OrderActionResult
        {
            OrderId = order.Id,
            Status = order.Status,
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

        var dispatchableStatuses = new HashSet<string>(StringComparer.Ordinal)
        {
            MarketplaceOrderStatuses.PaymentConfirmed,
            MarketplaceOrderStatuses.LabelGenerated
        };

        if (!dispatchableStatuses.Contains(order.Status))
            return ServiceResult<OrderActionResult>.Failure([new ValidationError("status", $"ORDER_NOT_DISPATCHABLE: {order.Status}")]);

        var nowUtc = DateTimeOffset.UtcNow;
        order.Status = MarketplaceOrderStatuses.Dispatched;
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
        var fulfillmentStatuses = new[] { MarketplaceOrderStatuses.PaymentConfirmed, MarketplaceOrderStatuses.LabelGenerated };
        var nowUtc = DateTimeOffset.UtcNow;

        var query = _dbContext.MarketplaceOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .Where(o => fulfillmentStatuses.Contains(o.Status))
            .OrderBy(o => o.ShipByDeadlineAt.HasValue ? 0 : 1)
            .ThenBy(o => o.ShipByDeadlineAt)
            .ThenBy(o => o.SabrPaymentConfirmedAt);

        var total = await query.CountAsync(cancellationToken);

        var orders = await query.Skip(skip).Take(limit).ToListAsync(cancellationToken);

        var clientIds = orders.Select(o => o.ClientId).Distinct().ToList();
        var clients = await _dbContext.Clients
            .AsNoTracking()
            .Where(c => clientIds.Contains(c.Id))
            .Select(c => new { c.Id, c.AccountName })
            .ToDictionaryAsync(c => c.Id, c => c.AccountName, cancellationToken);

        var shipmentLookup = await BuildShipmentLookupAsync(orders, cancellationToken);

        var items = orders.Select(o =>
        {
            var orderShipments = GetOrderShipments(o, shipmentLookup);
            return new AdminFulfillmentOrderResult
            {
                Id                      = o.Id,
                TenantId                = o.TenantId,
                ClientId                = o.ClientId,
                ClientName              = clients.GetValueOrDefault(o.ClientId),
                MlOrderId               = o.MlOrderId,
                SellerId                = o.SellerId.ToString(),
                ShipmentId              = o.ShipmentId,
                ShippingMode            = o.ShippingMode,
                LogisticType            = o.LogisticType,
                ShipByDeadlineAt        = o.ShipByDeadlineAt,
                IsUrgent                = o.ShipByDeadlineAt.HasValue && o.ShipByDeadlineAt.Value <= nowUtc.AddHours(4),
                HasLabel                = orderShipments.Any(item => item.Shipment.LabelContentBytes != null && item.Shipment.LabelContentBytes.Length > 0),
                TotalItems              = o.Items.Count,
                SabrPaymentConfirmedAt  = o.SabrPaymentConfirmedAt ?? o.CreatedAt,
                Shipments               = orderShipments.Select(MapShipmentResult).ToList(),
                InternalFulfillmentSummary = BuildInternalSummary(orderShipments)
            };
        }).ToList();

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
            HasLabel = shipment.Shipment.LabelContentBytes != null && shipment.Shipment.LabelContentBytes.Length > 0,
            Milestones = shipment.Milestones
        };
    }

    private static MarketplaceInternalFulfillmentSummaryResult BuildInternalSummary(IReadOnlyCollection<OrderShipmentSnapshot> shipments)
    {
        var milestones = new MarketplaceShipmentMilestonesResult
        {
            ProcessingStartedAt = shipments
                .Select(item => item.Milestones.ProcessingStartedAt)
                .Where(item => item.HasValue)
                .Select(item => item!.Value)
                .DefaultIfEmpty()
                .Max(),
            LabelPrintedAt = shipments
                .Select(item => item.Milestones.LabelPrintedAt)
                .Where(item => item.HasValue)
                .Select(item => item!.Value)
                .DefaultIfEmpty()
                .Max(),
            SeparatedAt = shipments
                .Select(item => item.Milestones.SeparatedAt)
                .Where(item => item.HasValue)
                .Select(item => item!.Value)
                .DefaultIfEmpty()
                .Max(),
            DispatchedAt = shipments
                .Select(item => item.Milestones.DispatchedAt)
                .Where(item => item.HasValue)
                .Select(item => item!.Value)
                .DefaultIfEmpty()
                .Max()
        };

        var stage = milestones.DispatchedAt.HasValue
            ? MarketplaceShipmentMilestones.Dispatched
            : milestones.SeparatedAt.HasValue
                ? MarketplaceShipmentMilestones.Separated
                : milestones.LabelPrintedAt.HasValue
                    ? MarketplaceShipmentMilestones.LabelPrinted
                    : milestones.ProcessingStartedAt.HasValue
                        ? MarketplaceShipmentMilestones.ProcessingStarted
                        : "pending";

        return new MarketplaceInternalFulfillmentSummaryResult
        {
            Stage = stage,
            Label = stage switch
            {
                MarketplaceShipmentMilestones.Dispatched => "Despachado",
                MarketplaceShipmentMilestones.Separated => "Pedido separado",
                MarketplaceShipmentMilestones.LabelPrinted => "Etiqueta impressa",
                MarketplaceShipmentMilestones.ProcessingStarted => "Em processamento",
                _ => "Aguardando processamento"
            },
            Milestones = new MarketplaceShipmentMilestonesResult
            {
                ProcessingStartedAt = milestones.ProcessingStartedAt == default ? null : milestones.ProcessingStartedAt,
                LabelPrintedAt = milestones.LabelPrintedAt == default ? null : milestones.LabelPrintedAt,
                SeparatedAt = milestones.SeparatedAt == default ? null : milestones.SeparatedAt,
                DispatchedAt = milestones.DispatchedAt == default ? null : milestones.DispatchedAt
            }
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
        var dispatchableStatuses = new HashSet<string>(StringComparer.Ordinal)
        {
            MarketplaceOrderStatuses.PaymentConfirmed,
            MarketplaceOrderStatuses.LabelGenerated,
            MarketplaceOrderStatuses.Dispatched
        };

        if (!dispatchableStatuses.Contains(order.Status))
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

        var allShipmentIds = await GetConcreteShipmentIdsAsync(order, cancellationToken);
        var allDispatched = allShipmentIds.All(candidate =>
            candidate == shipmentId ||
            _dbContext.MarketplaceEventLogs.AsNoTracking().Any(item =>
                item.TenantId == order.TenantId
                && item.ClientId == order.ClientId
                && item.Provider == order.Provider
                && item.Topic == MarketplaceEventTopics.AuditFulfillmentDispatched
                && item.ResourceId == candidate));

        if (allDispatched)
        {
            order.Status = MarketplaceOrderStatuses.Dispatched;
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
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<OrderActionResult>.Success(new OrderActionResult
        {
            OrderId = order.Id,
            Status = order.Status,
            UpdatedAt = order.UpdatedAt
        });
    }

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
}
