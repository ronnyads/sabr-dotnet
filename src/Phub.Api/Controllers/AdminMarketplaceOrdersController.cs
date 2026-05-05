using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Application.Validation;
using Phub.Domain.Enums;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/orders")]
public sealed class AdminMarketplaceOrdersController : ControllerBase
{
    private readonly ILogger<AdminMarketplaceOrdersController> _logger;
    private readonly MarketplaceOrderPaymentService _paymentService;
    private readonly OrderCancellationService _cancellationService;
    private readonly OrderFulfillmentService _fulfillmentService;

    public AdminMarketplaceOrdersController(
        ILogger<AdminMarketplaceOrdersController> logger,
        MarketplaceOrderPaymentService paymentService,
        OrderCancellationService cancellationService,
        OrderFulfillmentService fulfillmentService)
    {
        _logger = logger;
        _paymentService = paymentService;
        _cancellationService = cancellationService;
        _fulfillmentService = fulfillmentService;
    }

    [HttpGet]
    public async Task<IActionResult> ListOrders(
        [FromQuery] string? status = null,
        [FromQuery] string? internalStatus = null,
        [FromQuery] string? channelStatus = null,
        [FromQuery] string? tenantId = null,
        [FromQuery] string? provider = null,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);

        MarketplaceProvider? parsedProvider = null;
        if (!string.IsNullOrWhiteSpace(provider)
            && Enum.TryParse<MarketplaceProvider>(provider, ignoreCase: true, out var providerValue))
        {
            parsedProvider = providerValue;
        }

        var result = await _fulfillmentService.ListAdminOrdersAsync(
            status,
            internalStatus,
            channelStatus,
            tenantId,
            parsedProvider,
            DateTimeOffset.TryParse(from, out var fromDate) ? fromDate : null,
            DateTimeOffset.TryParse(to, out var toDate) ? toDate : null,
            skip,
            limit,
            cancellationToken);

        return Ok(result);
    }

    [HttpGet("{orderId:guid}")]
    public async Task<IActionResult> GetOrder(
        [FromRoute] Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var result = await _fulfillmentService.GetAdminOrderAsync(orderId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        return Ok(result.Data);
    }

    [HttpPost("{orderId:guid}/confirm-payment")]
    public async Task<IActionResult> ConfirmPayment(
        [FromRoute] Guid orderId,
        [FromBody] MarketplaceMarkPaidRequest? request,
        CancellationToken cancellationToken = default)
    {
        var orderResult = await _fulfillmentService.GetAdminOrderAsync(orderId, cancellationToken);
        if (!orderResult.Succeeded || orderResult.Data == null)
            return MapValidationError(orderResult.Errors);

        var result = await _paymentService.MarkPaidAsync(
            orderResult.Data.TenantId,
            orderResult.Data.ClientId,
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

    [HttpGet("{orderId:guid}/label")]
    public async Task<IActionResult> GetLabel(
        [FromRoute] Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var result = await _fulfillmentService.GetLabelAsync(orderId, null, null, cancellationToken: cancellationToken);
        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        return File(result.Data.Content, result.Data.ContentType, result.Data.FileName);
    }

    [HttpGet("{orderId:guid}/labels")]
    public async Task<IActionResult> ListLabels(
        [FromRoute] Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var result = await _fulfillmentService.ListLabelsAsync(orderId, null, null, cancellationToken);
        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        return Ok(result.Data);
    }

    [HttpGet("{orderId:guid}/labels/{shipmentId}")]
    public async Task<IActionResult> GetLabelByShipment(
        [FromRoute] Guid orderId,
        [FromRoute] string shipmentId,
        CancellationToken cancellationToken = default)
    {
        var result = await _fulfillmentService.GetLabelAsync(orderId, null, null, shipmentId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        return File(result.Data.Content, result.Data.ContentType, result.Data.FileName);
    }

    [HttpPost("{orderId:guid}/labels/pull")]
    public async Task<IActionResult> PullLabel(
        [FromRoute] Guid orderId,
        [FromQuery] string? shipmentId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _fulfillmentService.PullLabelAsync(orderId, null, null, shipmentId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        return Ok(result.Data);
    }

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

    [HttpPost("{orderId:guid}/shipments/{shipmentId}/milestone")]
    public async Task<IActionResult> AdvanceShipmentMilestone(
        [FromRoute] Guid orderId,
        [FromRoute] string shipmentId,
        [FromBody] MarketplaceShipmentMilestoneAdvanceRequest? request,
        CancellationToken cancellationToken = default)
    {
        var adminId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "admin";
        var result = await _fulfillmentService.AdvanceShipmentMilestoneAsync(
            orderId,
            shipmentId,
            request?.Milestone ?? string.Empty,
            adminId,
            cancellationToken);

        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        return Ok(result.Data);
    }

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

    [HttpPost("{orderId:guid}/cancellation-request/approve")]
    public async Task<IActionResult> ApproveCancellationRequest(
        [FromRoute] Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var adminId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "admin";
        var result = await _cancellationService.ApproveCancellationRequestAsync(orderId, adminId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        return Ok(result.Data);
    }

    [HttpPost("{orderId:guid}/cancellation-request/reject")]
    public async Task<IActionResult> RejectCancellationRequest(
        [FromRoute] Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var adminId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "admin";
        var result = await _cancellationService.RejectCancellationRequestAsync(orderId, adminId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
            return MapValidationError(result.Errors);

        return Ok(result.Data);
    }

    private IActionResult MapValidationError(IReadOnlyCollection<ValidationError> errors)
    {
        if (errors.Any(e => e.Message.Contains("NOT_FOUND")))
            return NotFound(CreateApiError("NOT_FOUND", errors.First().Message));
        if (errors.Any(e => e.Message.Contains("NOT_CANCELLABLE")
                            || e.Message.Contains("NOT_DISPATCHABLE")
                            || e.Message.Contains("NOT_REFUNDABLE")
                            || e.Message.Contains("NOT_PENDING")))
        {
            return UnprocessableEntity(CreateApiError("INVALID_TRANSITION", errors.First().Message));
        }

        if (errors.Any(e => string.Equals(e.Message, "LABEL_REQUIRED_BEFORE_PAYMENT", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("LABEL_REQUIRED_BEFORE_PAYMENT", "A etiqueta precisa estar disponível para confirmar o pagamento.", errors));
        }

        return BadRequest(CreateApiError("VALIDATION_ERROR", errors.FirstOrDefault()?.Message ?? "Invalid request", errors));
    }

    private ApiError CreateApiError(string code, string message, object? errors = null) =>
        new() { Code = code, Message = message, Errors = errors, TraceId = HttpContext.TraceIdentifier };
}
