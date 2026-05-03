using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
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
    /// Retorna a etiqueta de envio para um pedido (busca via ShipmentId).
    /// </summary>
    public async Task<ServiceResult<MarketplaceShipmentLabelDownloadResult>> GetLabelAsync(
        Guid orderId,
        string? tenantId,
        Guid? clientId,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.MarketplaceOrders.AsNoTracking().Where(o => o.Id == orderId);
        if (!string.IsNullOrWhiteSpace(tenantId))
            query = query.Where(o => o.TenantId == tenantId);
        if (clientId.HasValue && clientId != Guid.Empty)
            query = query.Where(o => o.ClientId == clientId.Value);

        var order = await query.FirstOrDefaultAsync(cancellationToken);
        if (order == null)
            return ServiceResult<MarketplaceShipmentLabelDownloadResult>.Failure([new ValidationError("orderId", "ORDER_NOT_FOUND")]);

        if (string.IsNullOrWhiteSpace(order.ShipmentId))
            return ServiceResult<MarketplaceShipmentLabelDownloadResult>.Failure([new ValidationError("shipmentId", "SHIPMENT_NOT_AVAILABLE")]);

        return await _labelService.GetOrFetchAsync(order.TenantId, order.ClientId, order.ShipmentId, cancellationToken);
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

        var shipmentIds = orders
            .Where(o => !string.IsNullOrWhiteSpace(o.ShipmentId))
            .Select(o => o.ShipmentId!)
            .Distinct()
            .ToList();
        var shipmentsWithLabel = await _dbContext.MarketplaceShipments
            .AsNoTracking()
            .Where(s => shipmentIds.Contains(s.ShipmentId) && s.LabelContentBytes != null)
            .Select(s => s.ShipmentId)
            .ToListAsync(cancellationToken);
        var labelShipmentSet = new HashSet<string>(shipmentsWithLabel, StringComparer.Ordinal);

        var items = orders.Select(o => new AdminFulfillmentOrderResult
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
            HasLabel                = !string.IsNullOrWhiteSpace(o.ShipmentId) && labelShipmentSet.Contains(o.ShipmentId!),
            TotalItems              = o.Items.Count,
            SabrPaymentConfirmedAt  = o.SabrPaymentConfirmedAt ?? o.CreatedAt
        }).ToList();

        return new PagedResult<AdminFulfillmentOrderResult>
        {
            Items = items,
            Total = total,
            Skip  = skip,
            Limit = limit
        };
    }
}
