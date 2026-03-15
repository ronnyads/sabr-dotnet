using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Services;
using Sabr.Application.Validation;
using Sabr.Domain.Enums;

namespace Sabr.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/orders")]
public sealed class AdminMarketplaceOrdersController : ControllerBase
{
    private readonly ILogger<AdminMarketplaceOrdersController> _logger;
    private readonly IAppDbContext _dbContext;
    private readonly MarketplaceOrderPaymentService _paymentService;
    private readonly OrderCancellationService _cancellationService;
    private readonly OrderFulfillmentService _fulfillmentService;

    public AdminMarketplaceOrdersController(
        ILogger<AdminMarketplaceOrdersController> logger,
        IAppDbContext dbContext,
        MarketplaceOrderPaymentService paymentService,
        OrderCancellationService cancellationService,
        OrderFulfillmentService fulfillmentService)
    {
        _logger = logger;
        _dbContext = dbContext;
        _paymentService = paymentService;
        _cancellationService = cancellationService;
        _fulfillmentService = fulfillmentService;
    }

    // GET /api/v1/admin/orders
    [HttpGet]
    public async Task<IActionResult> ListOrders(
        [FromQuery] string? status = null,
        [FromQuery] string? tenantId = null,
        [FromQuery] string? provider = null,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);

        var query = _dbContext.MarketplaceOrders.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(o => o.Status == status);
        if (!string.IsNullOrWhiteSpace(tenantId))
            query = query.Where(o => o.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(provider) &&
            Enum.TryParse<MarketplaceProvider>(provider, ignoreCase: true, out var parsedProvider))
            query = query.Where(o => o.Provider == parsedProvider);
        if (DateTimeOffset.TryParse(from, out var fromDate))
            query = query.Where(o => o.CreatedAt >= fromDate);
        if (DateTimeOffset.TryParse(to, out var toDate))
            query = query.Where(o => o.CreatedAt <= toDate);

        var total = await query.CountAsync(cancellationToken);

        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip(skip).Take(limit)
            .Include(o => o.Items)
            .ToListAsync(cancellationToken);

        var clientIds = orders.Select(o => o.ClientId).Distinct().ToList();
        var clients = await _dbContext.Clients
            .AsNoTracking()
            .Where(c => clientIds.Contains(c.Id))
            .Select(c => new { c.Id, c.AccountName })
            .ToDictionaryAsync(c => c.Id, c => c.AccountName, cancellationToken);

        var shipmentIds = orders
            .Where(o => !string.IsNullOrWhiteSpace(o.ShipmentId))
            .Select(o => o.ShipmentId!)
            .Distinct().ToList();
        var labelSet = await _dbContext.MarketplaceShipments
            .AsNoTracking()
            .Where(s => shipmentIds.Contains(s.ShipmentId) && s.LabelContentBytes != null)
            .Select(s => s.ShipmentId)
            .ToListAsync(cancellationToken);
        var labelShipmentSet = new HashSet<string>(labelSet, StringComparer.Ordinal);

        var items = orders.Select(o => new AdminOrderListItemResult
        {
            Id                     = o.Id,
            TenantId               = o.TenantId,
            ClientId               = o.ClientId,
            ClientName             = clients.GetValueOrDefault(o.ClientId),
            Provider               = o.Provider,
            SellerId               = o.SellerId.ToString(),
            MlOrderId              = o.MlOrderId,
            Status                 = o.Status,
            PaidAt                 = o.PaidAt,
            SabrPaymentConfirmedAt = o.SabrPaymentConfirmedAt,
            ShipmentId             = o.ShipmentId,
            ShippingMode           = o.ShippingMode,
            LogisticType           = o.LogisticType,
            ShipByDeadlineAt       = o.ShipByDeadlineAt,
            HasUnmappedItems       = o.Items.Any(i => i.MappingState == MarketplaceMappingStates.Unmapped),
            TotalItems             = o.Items.Count,
            HasLabel               = !string.IsNullOrWhiteSpace(o.ShipmentId) && labelShipmentSet.Contains(o.ShipmentId!),
            RiskFlagsJson          = o.RiskFlagsJson,
            ImportedAt             = o.ImportedAt
        }).ToList();

        return Ok(new PagedResult<AdminOrderListItemResult>
        {
            Items = items,
            Total = total,
            Skip  = skip,
            Limit = limit
        });
    }

    // GET /api/v1/admin/orders/{orderId}
    [HttpGet("{orderId:guid}")]
    public async Task<IActionResult> GetOrder(
        [FromRoute] Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.MarketplaceOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

        if (order == null)
            return NotFound(CreateApiError("ORDER_NOT_FOUND", "Pedido não encontrado"));

        var client = await _dbContext.Clients
            .AsNoTracking()
            .Where(c => c.Id == order.ClientId)
            .Select(c => new { c.Id, c.AccountName })
            .FirstOrDefaultAsync(cancellationToken);

        var shipmentIds = string.IsNullOrWhiteSpace(order.ShipmentId)
            ? []
            : new[] { order.ShipmentId };
        var hasLabel = shipmentIds.Length > 0 && await _dbContext.MarketplaceShipments
            .AsNoTracking()
            .AnyAsync(s => s.ShipmentId == order.ShipmentId && s.LabelContentBytes != null, cancellationToken);

        var variantSkus = order.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.SabrVariantSku))
            .Select(i => i.SabrVariantSku!)
            .Distinct().ToList();
        var products = await _dbContext.ProductVariants
            .AsNoTracking()
            .Where(v => variantSkus.Contains(v.VariantSku))
            .Select(v => new { v.VariantSku, v.Name })
            .ToDictionaryAsync(v => v.VariantSku, v => v.Name, cancellationToken);

        var result = new AdminOrderDetailResult
        {
            Id                     = order.Id,
            TenantId               = order.TenantId,
            ClientId               = order.ClientId,
            ClientName             = client?.AccountName,
            Provider               = order.Provider,
            SellerId               = order.SellerId.ToString(),
            MlOrderId              = order.MlOrderId,
            Status                 = order.Status,
            PaidAt                 = order.PaidAt,
            SabrPaymentConfirmedAt = order.SabrPaymentConfirmedAt,
            ShipmentId             = order.ShipmentId,
            ShippingMode           = order.ShippingMode,
            LogisticType           = order.LogisticType,
            ShipByDeadlineAt       = order.ShipByDeadlineAt,
            HasUnmappedItems       = order.Items.Any(i => i.MappingState == MarketplaceMappingStates.Unmapped),
            TotalItems             = order.Items.Count,
            HasLabel               = hasLabel,
            RiskFlagsJson          = order.RiskFlagsJson,
            ImportedAt             = order.ImportedAt,
            Items = order.Items.Select(i => new AdminOrderItemResult
            {
                Id               = i.Id,
                MlItemId         = i.MlItemId,
                MlVariationId    = i.MlVariationId,
                SabrVariantSku   = i.SabrVariantSku,
                ProductName      = i.SabrVariantSku != null ? products.GetValueOrDefault(i.SabrVariantSku) : null,
                Quantity         = i.Quantity,
                ReservedQuantity = i.ReservedQuantity,
                MappingState     = i.MappingState
            }).ToList()
        };

        return Ok(result);
    }

    // POST /api/v1/admin/orders/{orderId}/confirm-payment
    [HttpPost("{orderId:guid}/confirm-payment")]
    public async Task<IActionResult> ConfirmPayment(
        [FromRoute] Guid orderId,
        [FromBody] MarketplaceMarkPaidRequest? request,
        CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.MarketplaceOrders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order == null)
            return NotFound(CreateApiError("ORDER_NOT_FOUND", "Pedido não encontrado"));

        var result = await _paymentService.MarkPaidAsync(
            order.TenantId,
            order.ClientId,
            orderId,
            request?.Force ?? false,
            cancellationToken);

        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        if (result.Data.ConfirmationRequired && result.Data.Confirmation != null)
        {
            return Conflict(CreateApiError(
                "PAYMENT_CONFIRMATION_REQUIRED",
                "Pagamento após prazo/corte. Confirmação obrigatória.",
                result.Data.Confirmation));
        }

        return Ok(result.Data.Result);
    }

    // POST /api/v1/admin/orders/{orderId}/cancel
    [HttpPost("{orderId:guid}/cancel")]
    public async Task<IActionResult> CancelOrder(
        [FromRoute] Guid orderId,
        [FromBody] OrderCancelRequest? request,
        CancellationToken cancellationToken = default)
    {
        var adminId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "admin";
        var result = await _cancellationService.CancelOrderAsync(
            orderId,
            tenantId: null,
            clientId: null,
            reason: request?.Reason,
            cancelledBy: adminId,
            cancellationToken);

        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        return Ok(result.Data);
    }

    // POST /api/v1/admin/orders/{orderId}/refund
    [HttpPost("{orderId:guid}/refund")]
    public async Task<IActionResult> ProcessRefund(
        [FromRoute] Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var adminId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "admin";
        var result = await _cancellationService.ProcessRefundAsync(orderId, adminId, cancellationToken);

        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        return Ok(result.Data);
    }

    // GET /api/v1/admin/orders/{orderId}/label
    [HttpGet("{orderId:guid}/label")]
    public async Task<IActionResult> GetLabel(
        [FromRoute] Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var result = await _fulfillmentService.GetLabelAsync(orderId, null, null, cancellationToken);
        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        var label = result.Data;
        return File(label.Content, label.ContentType, label.FileName);
    }

    // POST /api/v1/admin/orders/{orderId}/dispatch
    [HttpPost("{orderId:guid}/dispatch")]
    public async Task<IActionResult> DispatchOrder(
        [FromRoute] Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var adminId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "admin";
        var result = await _fulfillmentService.MarkDispatchedAsync(orderId, adminId, cancellationToken);

        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        return Ok(result.Data);
    }

    // GET /api/v1/admin/fulfillment
    [HttpGet("/api/v1/admin/fulfillment")]
    public async Task<IActionResult> ListFulfillment(
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var result = await _fulfillmentService.ListFulfillmentAsync(skip, limit, cancellationToken);
        return Ok(result);
    }

    private IActionResult MapValidationError(IReadOnlyCollection<ValidationError> errors)
    {
        if (errors.Any(e => e.Message.Contains("NOT_FOUND")))
            return NotFound(CreateApiError("NOT_FOUND", errors.First().Message));
        if (errors.Any(e => e.Message.Contains("NOT_CANCELLABLE") || e.Message.Contains("NOT_DISPATCHABLE") || e.Message.Contains("NOT_REFUNDABLE")))
            return UnprocessableEntity(CreateApiError("INVALID_TRANSITION", errors.First().Message));
        return BadRequest(CreateApiError("VALIDATION_ERROR", errors.FirstOrDefault()?.Message ?? "Invalid request", errors));
    }

    private ApiError CreateApiError(string code, string message, object? errors = null) =>
        new() { Code = code, Message = message, Errors = errors, TraceId = HttpContext.TraceIdentifier };
}
