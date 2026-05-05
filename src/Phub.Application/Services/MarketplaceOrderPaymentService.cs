using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Options;
using Phub.Application.Validation;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class MarketplaceOrderPaymentService
{
    private readonly IAppDbContext _dbContext;
    private readonly StockAvailabilityService _stockAvailabilityService;
    private readonly MarketplaceAuditLogService _auditLogService;
    private readonly MercadoLivreOptions _options;
    private readonly TinyIntegrationService _tinyIntegrationService;

    public MarketplaceOrderPaymentService(
        IAppDbContext dbContext,
        StockAvailabilityService stockAvailabilityService,
        MarketplaceAuditLogService auditLogService,
        IOptions<MercadoLivreOptions> options,
        TinyIntegrationService tinyIntegrationService)
    {
        _dbContext = dbContext;
        _stockAvailabilityService = stockAvailabilityService;
        _auditLogService = auditLogService;
        _options = options.Value;
        _tinyIntegrationService = tinyIntegrationService;
    }

    public async Task<ServiceResult<MarketplaceMarkPaidExecutionResult>> MarkPaidAsync(
        string tenantId,
        Guid clientId,
        Guid orderId,
        bool force,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty || orderId == Guid.Empty)
        {
            return ServiceResult<MarketplaceMarkPaidExecutionResult>.Failure(new[]
            {
                new ValidationError("context", "Invalid tenant/client/order context")
            });
        }

        var order = await _dbContext.MarketplaceOrders
            .FirstOrDefaultAsync(item => item.Id == orderId
                                         && item.TenantId == tenantId
                                         && item.ClientId == clientId,
                cancellationToken);
        if (order == null)
        {
            return ServiceResult<MarketplaceMarkPaidExecutionResult>.Failure(new[]
            {
                new ValidationError("orderId", "Order not found")
            });
        }

        if (order.SabrPaymentConfirmedAt.HasValue)
        {
            return ServiceResult<MarketplaceMarkPaidExecutionResult>.Success(new MarketplaceMarkPaidExecutionResult
            {
                ConfirmationRequired = false,
                Result = new MarketplaceMarkPaidResult
                {
                    OrderId = order.Id,
                    AlreadyPaid = true,
                    SabrPaymentConfirmedAt = order.SabrPaymentConfirmedAt,
                    RiskFlagsJson = order.RiskFlagsJson
                }
            });
        }

        var orderItems = await _dbContext.MarketplaceOrderItems
            .Where(item => item.MarketplaceOrderId == order.Id)
            .ToListAsync(cancellationToken);
        if (orderItems.Any(item => !string.Equals(item.MappingState, MarketplaceMappingStates.Mapped, StringComparison.Ordinal)))
        {
            return ServiceResult<MarketplaceMarkPaidExecutionResult>.Failure(new[]
            {
                new ValidationError("mapping", "ML_UNMAPPED_ITEM")
            });
        }

        if (MarketplaceOrderWorkflow.RequiresLabelForPayment(order.Provider))
        {
            var shipments = await _dbContext.MarketplaceShipments
                .AsNoTracking()
                .Where(item => item.TenantId == tenantId
                               && item.ClientId == clientId
                               && item.Provider == order.Provider
                               && item.MlOrderId == order.MlOrderId)
                .ToListAsync(cancellationToken);

            if (!MarketplaceOrderWorkflow.CanMarkPaid(order, shipments))
            {
                return ServiceResult<MarketplaceMarkPaidExecutionResult>.Failure(new[]
                {
                    new ValidationError("label", "LABEL_REQUIRED_BEFORE_PAYMENT")
                });
            }
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var riskEvaluation = await EvaluateRiskReasonsAsync(order, nowUtc, cancellationToken);
        var riskReasons = riskEvaluation.reasons;
        var confirmationPayload = riskEvaluation.confirmationPayload;
        if (riskReasons.Count > 0 && !force)
        {
            return ServiceResult<MarketplaceMarkPaidExecutionResult>.Success(new MarketplaceMarkPaidExecutionResult
            {
                ConfirmationRequired = true,
                Confirmation = confirmationPayload
            });
        }

        var reservations = await _dbContext.StockReservations
            .Where(item => item.MarketplaceOrderId == order.Id
                           && item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Status == StockReservationStatus.Reserved)
            .OrderBy(item => item.ReservedAt)
            .ToListAsync(cancellationToken);

        var consumedBySku = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var reservation in reservations)
        {
            reservation.Status = StockReservationStatus.Consumed;
            reservation.UpdatedAt = nowUtc;

            if (!consumedBySku.TryGetValue(reservation.SabrVariantSku, out var qty))
            {
                qty = 0;
            }

            consumedBySku[reservation.SabrVariantSku] = qty + reservation.Quantity;
        }

        foreach (var item in orderItems)
        {
            item.ReservedQuantity = Math.Max(0, item.ReservedQuantity);
            item.UpdatedAt = nowUtc;
        }

        foreach (var (sku, quantity) in consumedBySku)
        {
            var variant = await _dbContext.ProductVariants.FirstOrDefaultAsync(
                item => item.VariantSku == sku,
                cancellationToken);
            if (variant == null)
            {
                continue;
            }

            variant.PhysicalStock = Math.Max(0, variant.PhysicalStock - quantity);
            variant.ReservedStock = Math.Max(0, variant.ReservedStock - quantity);
            variant.AvailableStock = StockAvailabilityService.ComputeAvailable(variant);
        }

        order.SabrPaymentConfirmedAt = nowUtc;
        order.PaidAt ??= nowUtc;
        if (riskReasons.Count > 0)
        {
            order.RiskFlagsJson = JsonSerializer.Serialize(new
            {
                reasons = riskReasons
            });
        }

        order.UpdatedAt = nowUtc;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditLogService.RecordAsync(
            order.TenantId,
            order.ClientId,
            order.Provider,
            order.SellerId,
            MarketplaceEventTopics.AuditOrderPaid,
            order.MlOrderId,
            new
            {
                orderId = order.MlOrderId,
                paidAt = order.SabrPaymentConfirmedAt,
                risk = order.RiskFlagsJson
            },
            "v1",
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (consumedBySku.Count > 0)
        {
            await _stockAvailabilityService.SyncStockForSkusAsync(
                tenantId,
                clientId,
                consumedBySku.Keys,
                cancellationToken);

            if (order.Provider == MarketplaceProvider.TinyErp)
            {
                foreach (var sku in consumedBySku.Keys)
                {
                    _ = _tinyIntegrationService.PushStockToTinyAsync(tenantId, clientId, sku);
                }
            }
        }

        return ServiceResult<MarketplaceMarkPaidExecutionResult>.Success(new MarketplaceMarkPaidExecutionResult
        {
            ConfirmationRequired = false,
            Result = new MarketplaceMarkPaidResult
            {
                OrderId = order.Id,
                AlreadyPaid = false,
                SabrPaymentConfirmedAt = order.SabrPaymentConfirmedAt,
                RiskFlagsJson = order.RiskFlagsJson
            }
        });
    }

    private async Task<(List<string> reasons, MarketplacePaymentConfirmationRequiredResult confirmationPayload)> EvaluateRiskReasonsAsync(
        Domain.Entities.MarketplaceOrder order,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var effectiveCutoff = await ResolveCutoffLocalTimeAsync(order, cancellationToken);
        var confirmationPayload = new MarketplacePaymentConfirmationRequiredResult
        {
            CutoffLocalTime = effectiveCutoff
        };

        var reasons = new List<string>();
        if (!order.ShipByDeadlineAt.HasValue)
        {
            return (reasons, confirmationPayload);
        }

        var tz = ResolveTimeZone(_options.DefaultTimeZoneId);
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, tz);
        var shipByLocal = TimeZoneInfo.ConvertTime(order.ShipByDeadlineAt.Value, tz);
        var cutoff = ParseCutoff(effectiveCutoff);
        var cutoffAt = new DateTimeOffset(
            shipByLocal.Year,
            shipByLocal.Month,
            shipByLocal.Day,
            cutoff.Hour,
            cutoff.Minute,
            0,
            nowLocal.Offset);

        if (nowLocal > cutoffAt || nowUtc > order.ShipByDeadlineAt.Value)
        {
            reasons.Add("PAID_AFTER_DEADLINE");
            confirmationPayload = new MarketplacePaymentConfirmationRequiredResult
            {
                ShipByDeadlineAt = order.ShipByDeadlineAt,
                CutoffLocalTime = effectiveCutoff,
                NowLocal = nowLocal.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                Message = "Pagamento apos prazo/corte. Confirmar para continuar."
            };
        }

        return (reasons, confirmationPayload);
    }

    private async Task<string> ResolveCutoffLocalTimeAsync(
        Domain.Entities.MarketplaceOrder order,
        CancellationToken cancellationToken)
    {
        if (!_options.Features.SlaByMode || string.IsNullOrWhiteSpace(order.LogisticType))
        {
            return _options.DefaultCutoffLocalTime;
        }

        var normalizedLogisticType = order.LogisticType.Trim().ToLowerInvariant();
        var normalizedShippingMode = string.IsNullOrWhiteSpace(order.ShippingMode)
            ? null
            : order.ShippingMode.Trim().ToLowerInvariant();

        var exact = await _dbContext.TenantMarketplaceSlaRules
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.TenantId == order.TenantId
                                         && item.ClientId == order.ClientId
                                         && item.Provider == order.Provider
                                         && item.LogisticType.ToLower() == normalizedLogisticType
                                         && item.ShippingMode != null
                                         && item.ShippingMode.ToLower() == normalizedShippingMode,
                cancellationToken);
        if (exact != null && !string.IsNullOrWhiteSpace(exact.CutoffLocalTime))
        {
            return exact.CutoffLocalTime;
        }

        var byLogistic = await _dbContext.TenantMarketplaceSlaRules
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.TenantId == order.TenantId
                                         && item.ClientId == order.ClientId
                                         && item.Provider == order.Provider
                                         && item.LogisticType.ToLower() == normalizedLogisticType
                                         && item.ShippingMode == null,
                cancellationToken);
        if (byLogistic != null && !string.IsNullOrWhiteSpace(byLogistic.CutoffLocalTime))
        {
            return byLogistic.CutoffLocalTime;
        }

        return _options.DefaultCutoffLocalTime;
    }

    private static TimeOnly ParseCutoff(string value)
    {
        if (TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        return new TimeOnly(12, 0);
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch
        {
            return TimeZoneInfo.Utc;
        }
    }
}
