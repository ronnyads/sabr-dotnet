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

    /// <summary>
    /// Cancela um pedido. Pode ser chamado pelo admin (sem restrição de tenant/client)
    /// ou pelo cliente (tenantId e clientId obrigatórios para escopo).
    /// Libera reservas de estoque e, se o pagamento já foi confirmado, restaura o estoque físico.
    /// </summary>
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

        if (!MarketplaceOrderStatuses.CancellableStatuses.Contains(order.Status))
            return ServiceResult<OrderActionResult>.Failure([new ValidationError("status", $"ORDER_NOT_CANCELLABLE: {order.Status}")]);

        var wasPaymentConfirmed = order.SabrPaymentConfirmedAt.HasValue;
        var nowUtc = DateTimeOffset.UtcNow;

        // 1. Liberar/restaurar reservas de estoque
        var reservations = await _dbContext.StockReservations
            .Where(r => r.MarketplaceOrderId == orderId)
            .ToListAsync(cancellationToken);

        var restoreBySku = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var reservation in reservations)
        {
            if (reservation.Status == StockReservationStatus.Consumed && wasPaymentConfirmed)
            {
                // Pagamento já foi confirmado → precisa restaurar estoque físico
                if (!restoreBySku.TryGetValue(reservation.SabrVariantSku, out var qty))
                    qty = 0;
                restoreBySku[reservation.SabrVariantSku] = qty + reservation.Quantity;
            }

            reservation.Status = StockReservationStatus.Released;
            reservation.UpdatedAt = nowUtc;
        }

        // 2. Se havia estoque consumido, restaurar PhysicalStock
        foreach (var (sku, quantity) in restoreBySku)
        {
            var variant = await _dbContext.ProductVariants
                .FirstOrDefaultAsync(v => v.VariantSku == sku, cancellationToken);
            if (variant == null) continue;

            variant.PhysicalStock += quantity;
            variant.AvailableStock = StockAvailabilityService.ComputeAvailable(variant);
        }

        // 3. Atualizar status do pedido
        order.Status = MarketplaceOrderStatuses.Cancelled;
        order.UpdatedAt = nowUtc;

        await _dbContext.SaveChangesAsync(cancellationToken);

        // 4. Audit log
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
            OrderId   = order.Id,
            Status    = order.Status,
            UpdatedAt = order.UpdatedAt
        });
    }

    /// <summary>
    /// Cliente solicita estorno. Muda status para refund_requested para aprovação admin.
    /// </summary>
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
            OrderId   = order.Id,
            Status    = order.Status,
            UpdatedAt = order.UpdatedAt
        });
    }

    /// <summary>
    /// Admin processa estorno aprovado. Muda status para refunded.
    /// </summary>
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
            OrderId   = order.Id,
            Status    = order.Status,
            UpdatedAt = order.UpdatedAt
        });
    }
}
