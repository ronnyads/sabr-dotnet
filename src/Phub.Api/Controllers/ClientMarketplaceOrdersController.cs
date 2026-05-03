using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Application.Validation;
using Phub.Domain.Enums;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/client/orders")]
public sealed class ClientMarketplaceOrdersController : ControllerBase
{
    private readonly ILogger<ClientMarketplaceOrdersController> _logger;
    private readonly ITenantProvider _tenantProvider;
    private readonly MercadoLivreIntegrationService _integrationService;
    private readonly MarketplaceOrderPaymentService _paymentService;
    private readonly OrderCancellationService _cancellationService;
    private readonly IAppDbContext _dbContext;

    public ClientMarketplaceOrdersController(
        ILogger<ClientMarketplaceOrdersController> logger,
        ITenantProvider tenantProvider,
        MercadoLivreIntegrationService integrationService,
        MarketplaceOrderPaymentService paymentService,
        OrderCancellationService cancellationService,
        IAppDbContext dbContext)
    {
        _logger = logger;
        _tenantProvider = tenantProvider;
        _integrationService = integrationService;
        _paymentService = paymentService;
        _cancellationService = cancellationService;
        _dbContext = dbContext;
    }

    // GET /api/v1/client/orders/marketplace
    [HttpGet("marketplace")]
    public async Task<IActionResult> ListMarketplaceOrders(
        [FromQuery] string provider = "MercadoLivre",
        [FromQuery] string? status = null,
        [FromQuery] string? logisticType = null,
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
            return error!;

        if (!Enum.TryParse<MarketplaceProvider>(provider, ignoreCase: true, out var parsedProvider))
            return BadRequest(CreateApiError("PROVIDER_INVALID", "Invalid marketplace provider"));

        try
        {
            var result = await _integrationService.ListOrdersAsync(
                tenantId!,
                clientId,
                parsedProvider,
                status,
                logisticType,
                skip,
                limit,
                cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to list marketplace orders. provider={Provider} tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                parsedProvider, tenantId, clientId, HttpContext.TraceIdentifier);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                CreateApiError("ML_ORDERS_INTERNAL_ERROR", "Falha interna ao carregar pedidos de marketplace"));
        }
    }

    // GET /api/v1/client/orders/marketplace/{orderId}
    [HttpGet("marketplace/{orderId:guid}")]
    public async Task<IActionResult> GetMarketplaceOrder(
        [FromRoute] Guid orderId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
            return error!;

        var order = await _dbContext.MarketplaceOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId
                && o.TenantId == tenantId
                && o.ClientId == clientId, cancellationToken);

        if (order == null)
            return NotFound(CreateApiError("ORDER_NOT_FOUND", "Pedido não encontrado"));

        var variantSkus = order.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.SabrVariantSku))
            .Select(i => i.SabrVariantSku!)
            .Distinct().ToList();
        var products = await _dbContext.ProductVariants
            .AsNoTracking()
            .Where(v => variantSkus.Contains(v.VariantSku))
            .Select(v => new { v.VariantSku, v.Name })
            .ToDictionaryAsync(v => v.VariantSku, v => v.Name, cancellationToken);

        return Ok(new
        {
            order.Id,
            order.Provider,
            SellerId = order.SellerId.ToString(),
            order.MlOrderId,
            order.Status,
            order.PaidAt,
            order.SabrPaymentConfirmedAt,
            order.ShipmentId,
            order.ShippingMode,
            order.LogisticType,
            order.ShipByDeadlineAt,
            order.ImportedAt,
            CanCancel = MarketplaceOrderStatuses.CancellableStatuses.Contains(order.Status),
            CanRefund = MarketplaceOrderStatuses.RefundableStatuses.Contains(order.Status),
            Items = order.Items.Select(i => new
            {
                i.Id,
                i.MlItemId,
                i.MlVariationId,
                i.SabrVariantSku,
                ProductName = i.SabrVariantSku != null ? products.GetValueOrDefault(i.SabrVariantSku) : null,
                i.Quantity,
                i.MappingState
            })
        });
    }

    // POST /api/v1/client/orders/{orderId}/mark-paid
    [HttpPost("{orderId:guid}/mark-paid")]
    public async Task<IActionResult> MarkPaid(
        [FromRoute] Guid orderId,
        [FromBody] MarketplaceMarkPaidRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
            return error!;

        var result = await _paymentService.MarkPaidAsync(
            tenantId!,
            clientId,
            orderId,
            request?.Force ?? false,
            cancellationToken);

        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        if (result.Data.ConfirmationRequired && result.Data.Confirmation != null)
        {
            return Conflict(CreateApiError(
                "PAYMENT_CONFIRMATION_REQUIRED",
                "Pagamento apos prazo/corte. Confirmacao obrigatoria.",
                result.Data.Confirmation));
        }

        return Ok(result.Data.Result);
    }

    // POST /api/v1/client/orders/marketplace/{orderId}/cancel
    [HttpPost("marketplace/{orderId:guid}/cancel")]
    public async Task<IActionResult> CancelOrder(
        [FromRoute] Guid orderId,
        [FromBody] OrderCancelRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
            return error!;

        var result = await _cancellationService.CancelOrderAsync(
            orderId,
            tenantId,
            clientId,
            request?.Reason,
            cancelledBy: $"client:{clientId}",
            cancellationToken);

        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        return Ok(result.Data);
    }

    // POST /api/v1/client/orders/marketplace/{orderId}/refund-request
    [HttpPost("marketplace/{orderId:guid}/refund-request")]
    public async Task<IActionResult> RequestRefund(
        [FromRoute] Guid orderId,
        [FromBody] OrderRefundRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
            return error!;

        var result = await _cancellationService.RequestRefundAsync(
            orderId,
            tenantId!,
            clientId,
            request?.Reason,
            cancellationToken);

        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        return Ok(result.Data);
    }

    private bool TryGetClientContext(out string? tenantId, out Guid clientId, out IActionResult? errorResult)
    {
        tenantId = _tenantProvider.TenantId;
        clientId = Guid.Empty;
        errorResult = null;

        var accountType = User.FindFirst("accountType")?.Value;
        if (!string.Equals(accountType, AccountTypes.Client, StringComparison.OrdinalIgnoreCase))
        {
            errorResult = Forbid();
            return false;
        }

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            errorResult = BadRequest(CreateApiError("TENANT_NOT_RESOLVED", "Tenant not resolved"));
            return false;
        }

        if (!Guid.TryParse(User.FindFirst("clientId")?.Value, out clientId))
        {
            errorResult = Unauthorized(CreateApiError("INVALID_CLIENT_CONTEXT", "Invalid client context"));
            return false;
        }

        return true;
    }

    private IActionResult MapValidationError(IReadOnlyCollection<ValidationError> errors)
    {
        if (errors.Count == 0)
            return BadRequest(CreateApiError("VALIDATION_ERROR", "Invalid request"));

        if (errors.Any(e => e.Message.Contains("NOT_FOUND")))
            return NotFound(CreateApiError("ORDER_NOT_FOUND", "Pedido não encontrado"));

        if (errors.Any(e => e.Message.Contains("NOT_CANCELLABLE") || e.Message.Contains("NOT_REFUNDABLE")))
            return UnprocessableEntity(CreateApiError("INVALID_TRANSITION", errors.First().Message));

        if (errors.Any(e => string.Equals(e.Message, "ML_UNMAPPED_ITEM", StringComparison.OrdinalIgnoreCase)))
            return UnprocessableEntity(CreateApiError("ML_UNMAPPED_ITEM", "Order has unmapped items", errors));

        return BadRequest(CreateApiError("VALIDATION_ERROR", "Invalid request", errors));
    }

    private ApiError CreateApiError(string code, string message, object? errors = null) =>
        new() { Code = code, Message = message, Errors = errors, TraceId = HttpContext.TraceIdentifier };
}
