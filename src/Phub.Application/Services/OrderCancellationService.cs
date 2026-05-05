using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class OrderCancellationService
{
    private readonly IAppDbContext _dbContext;
    private readonly StockAvailabilityService _stockAvailabilityService;
    private readonly MarketplaceAuditLogService _auditLogService;
    private readonly TinyIntegrationService _tinyIntegrationService;

    public OrderCancellationService(
        IAppDbContext dbContext,
        StockAvailabilityService stockAvailabilityService,
        MarketplaceAuditLogService auditLogService,
        TinyIntegrationService tinyIntegrationService)
    {
        _dbContext = dbContext;
        _stockAvailabilityService = stockAvailabilityService;
        _auditLogService = auditLogService;
        _tinyIntegrationService = tinyIntegrationService;
    }

    public async Task<ServiceResult<OrderActionResult>> CancelOrderAsync(
        Guid orderId,
        string? tenantId,
        Guid? clientId,
        string? reason,
        string cancelledBy,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.MarketplaceOrders.Where(o => o.Id == orderId);
        if (!string.IsNullOrWhiteSpace(tenantId))
            query = query.Where(o => o.TenantId == tenantId);
        if (clientId.HasValue && clientId != Guid.Empty)
            query = query.Where(o => o.ClientId == clientId.Value);

        var order = await query.FirstOrDefaultAsync(cancellationToken);
        if (order == null)
            return ServiceResult<OrderActionResult>.Failure([new ValidationError("orderId", "ORDER_NOT_FOUND")]);

        var currentInternalStage = await ResolveCurrentInternalStageAsync(order, cancellationToken);
        if (!MarketplaceOrderWorkflow.CanAutoCancel(currentInternalStage))
        {
            return await CreateCancellationRequestAsync(order, reason, cancelledBy, cancellationToken);
        }

        return await ExecuteCancellationAsync(order, reason, cancelledBy, cancellationToken);
    }

    public async Task<ServiceResult<OrderActionResult>> RequestRefundAsync(
        Guid orderId,
        string tenantId,
        Guid clientId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.MarketplaceOrders
            .FirstOrDefaultAsync(o => o.Id == orderId
                                      && o.TenantId == tenantId
                                      && o.ClientId == clientId, cancellationToken);

        if (order == null)
            return ServiceResult<OrderActionResult>.Failure([new ValidationError("orderId", "ORDER_NOT_FOUND")]);

        if (!MarketplaceOrderStatuses.RefundableStatuses.Contains(order.Status))
            return ServiceResult<OrderActionResult>.Failure([new ValidationError("status", $"ORDER_NOT_REFUNDABLE: {order.Status}")]);

        var nowUtc = DateTimeOffset.UtcNow;
        order.Status = MarketplaceOrderStatuses.RefundRequested;
        order.UpdatedAt = nowUtc;

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditLogService.RecordAsync(
            order.TenantId,
            order.ClientId,
            order.Provider,
            order.SellerId,
            MarketplaceEventTopics.AuditOrderRefundRequest,
            order.MlOrderId,
            new { orderId = order.MlOrderId, reason },
            "v1",
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<OrderActionResult>.Success(new OrderActionResult
        {
            OrderId = order.Id,
            Status = order.Status,
            Action = "refund_requested",
            UpdatedAt = order.UpdatedAt
        });
    }

    public async Task<ServiceResult<OrderActionResult>> ProcessRefundAsync(
        Guid orderId,
        string processedByAdminId,
        CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.MarketplaceOrders
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

        if (order == null)
            return ServiceResult<OrderActionResult>.Failure([new ValidationError("orderId", "ORDER_NOT_FOUND")]);

        if (!string.Equals(order.Status, MarketplaceOrderStatuses.RefundRequested, StringComparison.Ordinal)
            && !MarketplaceOrderStatuses.RefundableStatuses.Contains(order.Status))
        {
            return ServiceResult<OrderActionResult>.Failure([new ValidationError("status", $"ORDER_NOT_REFUNDABLE: {order.Status}")]);
        }

        var nowUtc = DateTimeOffset.UtcNow;
        order.Status = MarketplaceOrderStatuses.Refunded;
        order.UpdatedAt = nowUtc;

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditLogService.RecordAsync(
            order.TenantId,
            order.ClientId,
            order.Provider,
            order.SellerId,
            MarketplaceEventTopics.AuditOrderRefunded,
            order.MlOrderId,
            new { orderId = order.MlOrderId, processedByAdminId },
            "v1",
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<OrderActionResult>.Success(new OrderActionResult
        {
            OrderId = order.Id,
            Status = order.Status,
            Action = "refunded",
            UpdatedAt = order.UpdatedAt
        });
    }

    public async Task<ServiceResult<OrderActionResult>> ApproveCancellationRequestAsync(
        Guid orderId,
        string processedByAdminId,
        CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.MarketplaceOrders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order == null)
            return ServiceResult<OrderActionResult>.Failure([new ValidationError("orderId", "ORDER_NOT_FOUND")]);

        if (!string.Equals(order.CancellationRequestStatus, MarketplaceCancellationRequestStatuses.Requested, StringComparison.OrdinalIgnoreCase))
            return ServiceResult<OrderActionResult>.Failure([new ValidationError("status", "CANCELLATION_REQUEST_NOT_PENDING")]);

        order.CancellationRequestStatus = MarketplaceCancellationRequestStatuses.Approved;
        order.CancellationReviewedAt = DateTimeOffset.UtcNow;
        order.CancellationReviewedBy = processedByAdminId;
        order.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditLogService.RecordAsync(
            order.TenantId,
            order.ClientId,
            order.Provider,
            order.SellerId,
            MarketplaceEventTopics.AuditOrderCancellationApproved,
            order.MlOrderId,
            new { orderId = order.MlOrderId, processedByAdminId },
            "v1",
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return await ExecuteCancellationAsync(order, order.CancellationRequestReason, processedByAdminId, cancellationToken);
    }

    public async Task<ServiceResult<OrderActionResult>> RejectCancellationRequestAsync(
        Guid orderId,
        string processedByAdminId,
        CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.MarketplaceOrders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order == null)
            return ServiceResult<OrderActionResult>.Failure([new ValidationError("orderId", "ORDER_NOT_FOUND")]);

        if (!string.Equals(order.CancellationRequestStatus, MarketplaceCancellationRequestStatuses.Requested, StringComparison.OrdinalIgnoreCase))
            return ServiceResult<OrderActionResult>.Failure([new ValidationError("status", "CANCELLATION_REQUEST_NOT_PENDING")]);

        order.CancellationRequestStatus = MarketplaceCancellationRequestStatuses.Rejected;
        order.CancellationReviewedAt = DateTimeOffset.UtcNow;
        order.CancellationReviewedBy = processedByAdminId;
        order.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditLogService.RecordAsync(
            order.TenantId,
            order.ClientId,
            order.Provider,
            order.SellerId,
            MarketplaceEventTopics.AuditOrderCancellationRejected,
            order.MlOrderId,
            new { orderId = order.MlOrderId, processedByAdminId },
            "v1",
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<OrderActionResult>.Success(new OrderActionResult
        {
            OrderId = order.Id,
            Status = order.Status,
            Action = "cancellation_rejected",
            Message = "Solicitação de cancelamento recusada.",
            CancellationRequestStatus = order.CancellationRequestStatus,
            UpdatedAt = order.UpdatedAt
        });
    }

    private async Task<ServiceResult<OrderActionResult>> CreateCancellationRequestAsync(
        Domain.Entities.MarketplaceOrder order,
        string? reason,
        string requestedBy,
        CancellationToken cancellationToken)
    {
        order.CancellationRequestStatus = MarketplaceCancellationRequestStatuses.Requested;
        order.CancellationRequestedAt = DateTimeOffset.UtcNow;
        order.CancellationRequestedBy = requestedBy;
        order.CancellationRequestReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        order.CancellationReviewedAt = null;
        order.CancellationReviewedBy = null;
        order.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditLogService.RecordAsync(
            order.TenantId,
            order.ClientId,
            order.Provider,
            order.SellerId,
            MarketplaceEventTopics.AuditOrderCancellationRequested,
            order.MlOrderId,
            new { orderId = order.MlOrderId, reason, requestedBy },
            "v1",
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<OrderActionResult>.Success(new OrderActionResult
        {
            OrderId = order.Id,
            Status = order.Status,
            Action = "cancellation_requested",
            Message = "Pedido já está em operação. A solicitação foi enviada para revisão do admin.",
            CancellationRequestStatus = order.CancellationRequestStatus,
            UpdatedAt = order.UpdatedAt
        });
    }

    private async Task<string> ResolveCurrentInternalStageAsync(
        Domain.Entities.MarketplaceOrder order,
        CancellationToken cancellationToken)
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

        if (shipmentIds.Count == 0)
        {
            if (order.SabrPaymentConfirmedAt.HasValue)
                return MarketplaceInternalStages.ProcessingStarted;

            return order.ImportedAt != default
                ? MarketplaceInternalStages.Received
                : MarketplaceInternalStages.Pending;
        }

        var logs = await _dbContext.MarketplaceEventLogs
            .AsNoTracking()
            .Where(item => item.TenantId == order.TenantId
                           && item.ClientId == order.ClientId
                           && item.Provider == order.Provider
                           && shipmentIds.Contains(item.ResourceId)
                           && (item.Topic == MarketplaceEventTopics.AuditFulfillmentProcessingStarted
                               || item.Topic == MarketplaceEventTopics.AuditFulfillmentLabelPrinted
                               || item.Topic == MarketplaceEventTopics.AuditFulfillmentSeparated
                               || item.Topic == MarketplaceEventTopics.AuditFulfillmentDispatched))
            .ToListAsync(cancellationToken);

        if (logs.Any(item => item.Topic == MarketplaceEventTopics.AuditFulfillmentDispatched))
            return MarketplaceInternalStages.Dispatched;
        if (logs.Any(item => item.Topic == MarketplaceEventTopics.AuditFulfillmentSeparated))
            return MarketplaceInternalStages.Separated;
        if (logs.Any(item => item.Topic == MarketplaceEventTopics.AuditFulfillmentLabelPrinted))
            return MarketplaceInternalStages.LabelPrinted;
        if (logs.Any(item => item.Topic == MarketplaceEventTopics.AuditFulfillmentProcessingStarted) || order.SabrPaymentConfirmedAt.HasValue)
            return MarketplaceInternalStages.ProcessingStarted;
        if (order.ImportedAt != default)
            return MarketplaceInternalStages.Received;

        return MarketplaceInternalStages.Pending;
    }

    private async Task<ServiceResult<OrderActionResult>> ExecuteCancellationAsync(
        Domain.Entities.MarketplaceOrder order,
        string? reason,
        string cancelledBy,
        CancellationToken cancellationToken)
    {
        if (!MarketplaceOrderStatuses.CancellableStatuses.Contains(order.Status))
            return ServiceResult<OrderActionResult>.Failure([new ValidationError("status", $"ORDER_NOT_CANCELLABLE: {order.Status}")]);

        var wasPaymentConfirmed = order.SabrPaymentConfirmedAt.HasValue;
        var nowUtc = DateTimeOffset.UtcNow;

        var reservations = await _dbContext.StockReservations
            .Where(r => r.MarketplaceOrderId == order.Id)
            .ToListAsync(cancellationToken);

        var restoreBySku = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var reservation in reservations)
        {
            if (reservation.Status == StockReservationStatus.Consumed && wasPaymentConfirmed)
            {
                restoreBySku.TryGetValue(reservation.SabrVariantSku, out var qty);
                restoreBySku[reservation.SabrVariantSku] = qty + reservation.Quantity;
            }

            reservation.Status = StockReservationStatus.Released;
            reservation.UpdatedAt = nowUtc;
        }

        foreach (var (sku, quantity) in restoreBySku)
        {
            var variant = await _dbContext.ProductVariants
                .FirstOrDefaultAsync(v => v.VariantSku == sku, cancellationToken);
            if (variant == null) continue;

            variant.PhysicalStock += quantity;
            variant.AvailableStock = StockAvailabilityService.ComputeAvailable(variant);
        }

        order.Status = MarketplaceOrderStatuses.Cancelled;
        order.CancellationRequestStatus ??= MarketplaceCancellationRequestStatuses.Approved;
        order.CancellationReviewedAt ??= nowUtc;
        order.CancellationReviewedBy ??= cancelledBy;
        order.UpdatedAt = nowUtc;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditLogService.RecordAsync(
            order.TenantId,
            order.ClientId,
            order.Provider,
            order.SellerId,
            MarketplaceEventTopics.AuditOrderCancelled,
            order.MlOrderId,
            new { orderId = order.MlOrderId, reason, cancelledBy, wasPaymentConfirmed, stockRestored = restoreBySku },
            "v1",
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (restoreBySku.Count > 0)
        {
            await _stockAvailabilityService.SyncStockForSkusAsync(
                order.TenantId,
                order.ClientId,
                restoreBySku.Keys,
                cancellationToken);

            if (order.Provider == MarketplaceProvider.TinyErp)
            {
                foreach (var sku in restoreBySku.Keys)
                {
                    _ = _tinyIntegrationService.PushStockToTinyAsync(order.TenantId, order.ClientId, sku);
                }
            }
        }

        return ServiceResult<OrderActionResult>.Success(new OrderActionResult
        {
            OrderId = order.Id,
            Status = order.Status,
            Action = "cancelled",
            Message = "Pedido cancelado com sucesso.",
            CancellationRequestStatus = order.CancellationRequestStatus,
            UpdatedAt = order.UpdatedAt
        });
    }
}
