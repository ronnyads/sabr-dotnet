using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    private readonly OrderFulfillmentService _orderFulfillmentService;
    private readonly MarketplaceOrderPaymentService _paymentService;
    private readonly OrderCancellationService _cancellationService;

    public ClientMarketplaceOrdersController(
        ILogger<ClientMarketplaceOrdersController> logger,
        ITenantProvider tenantProvider,
        OrderFulfillmentService orderFulfillmentService,
        MarketplaceOrderPaymentService paymentService,
        OrderCancellationService cancellationService)
    {
        _logger = logger;
        _tenantProvider = tenantProvider;
        _orderFulfillmentService = orderFulfillmentService;
        _paymentService = paymentService;
        _cancellationService = cancellationService;
    }

    [HttpGet("marketplace")]
    public async Task<IActionResult> ListMarketplaceOrders(
        [FromQuery] string? provider = null,
        [FromQuery] string? status = null,
        [FromQuery] string? internalStatus = null,
        [FromQuery] string? channelStatus = null,
        [FromQuery] string? logisticType = null,
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
            return error!;

        MarketplaceProvider? parsedProvider = null;
        if (!string.IsNullOrWhiteSpace(provider))
        {
            if (!Enum.TryParse<MarketplaceProvider>(provider, ignoreCase: true, out var providerValue))
                return BadRequest(CreateApiError("PROVIDER_INVALID", "Invalid marketplace provider"));

            parsedProvider = providerValue;
        }

        try
        {
            var result = await _orderFulfillmentService.ListClientOrdersAsync(
                tenantId!,
                clientId,
                parsedProvider,
                internalStatus,
                channelStatus,
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
                CreateApiError("MARKETPLACE_ORDERS_INTERNAL_ERROR", "Falha interna ao carregar pedidos de marketplace"));
        }
    }

    [HttpGet("marketplace/{orderId:guid}")]
    public async Task<IActionResult> GetMarketplaceOrder(
        [FromRoute] Guid orderId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
            return error!;

        var result = await _orderFulfillmentService.GetClientOrderAsync(orderId, tenantId!, clientId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        return Ok(result.Data);
    }

    [HttpGet("marketplace/{orderId:guid}/labels")]
    public async Task<IActionResult> ListOrderLabels(
        [FromRoute] Guid orderId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
            return error!;

        var result = await _orderFulfillmentService.ListLabelsAsync(orderId, tenantId, clientId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        return Ok(result.Data);
    }

    [HttpGet("marketplace/{orderId:guid}/labels/{shipmentId}")]
    public async Task<IActionResult> DownloadOrderLabel(
        [FromRoute] Guid orderId,
        [FromRoute] string shipmentId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
            return error!;

        var result = await _orderFulfillmentService.GetLabelAsync(orderId, tenantId, clientId, shipmentId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        return File(result.Data.Content, result.Data.ContentType, result.Data.FileName);
    }

    [HttpPost("marketplace/{orderId:guid}/labels/pull")]
    public async Task<IActionResult> PullOrderLabel(
        [FromRoute] Guid orderId,
        [FromQuery] string? shipmentId = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
            return error!;

        var result = await _orderFulfillmentService.PullLabelAsync(orderId, tenantId, clientId, shipmentId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        return Ok(result.Data);
    }

    [HttpPost("marketplace/labels/pull")]
    public async Task<IActionResult> PullOrderLabelsBulk(
        [FromBody] MarketplacePullLabelsBulkRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
            return error!;

        var result = await _orderFulfillmentService.PullLabelsBulkAsync(
            tenantId!,
            clientId,
            request?.OrderIds ?? [],
            cancellationToken);
        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        return Ok(result.Data);
    }

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

        if (errors.Any(e => e.Message.Contains("NOT_FOUND") || e.Message.Contains("NOT_AVAILABLE")))
            return NotFound(CreateApiError("ORDER_NOT_FOUND", errors.First().Message));

        if (errors.Any(e => e.Message.Contains("NOT_CANCELLABLE") || e.Message.Contains("NOT_REFUNDABLE")))
            return UnprocessableEntity(CreateApiError("INVALID_TRANSITION", errors.First().Message));

        if (errors.Any(e => string.Equals(e.Message, "ML_UNMAPPED_ITEM", StringComparison.OrdinalIgnoreCase)))
            return UnprocessableEntity(CreateApiError("ML_UNMAPPED_ITEM", "Order has unmapped items", errors));
        if (errors.Any(e => string.Equals(e.Message, "LABEL_REQUIRED_BEFORE_PAYMENT", StringComparison.OrdinalIgnoreCase)))
            return UnprocessableEntity(CreateApiError("LABEL_REQUIRED_BEFORE_PAYMENT", "A etiqueta precisa estar disponível para confirmar o pagamento.", errors));

        return BadRequest(CreateApiError("VALIDATION_ERROR", "Invalid request", errors));
    }

    private ApiError CreateApiError(string code, string message, object? errors = null) =>
        new() { Code = code, Message = message, Errors = errors, TraceId = HttpContext.TraceIdentifier };
}
