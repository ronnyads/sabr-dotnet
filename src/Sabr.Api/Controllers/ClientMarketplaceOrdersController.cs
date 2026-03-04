using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Services;
using Sabr.Application.Validation;
using Sabr.Domain.Enums;

namespace Sabr.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/client/orders")]
public sealed class ClientMarketplaceOrdersController : ControllerBase
{
    private readonly ILogger<ClientMarketplaceOrdersController> _logger;
    private readonly ITenantProvider _tenantProvider;
    private readonly MercadoLivreIntegrationService _integrationService;
    private readonly MarketplaceOrderPaymentService _paymentService;

    public ClientMarketplaceOrdersController(
        ILogger<ClientMarketplaceOrdersController> logger,
        ITenantProvider tenantProvider,
        MercadoLivreIntegrationService integrationService,
        MarketplaceOrderPaymentService paymentService)
    {
        _logger = logger;
        _tenantProvider = tenantProvider;
        _integrationService = integrationService;
        _paymentService = paymentService;
    }

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
        {
            return error!;
        }

        if (!Enum.TryParse<MarketplaceProvider>(provider, ignoreCase: true, out var parsedProvider))
        {
            return BadRequest(CreateApiError("PROVIDER_INVALID", "Invalid marketplace provider"));
        }

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
                parsedProvider,
                tenantId,
                clientId,
                HttpContext.TraceIdentifier);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                CreateApiError("ML_ORDERS_INTERNAL_ERROR", "Falha interna ao carregar pedidos de marketplace"));
        }
    }

    [HttpPost("{orderId:guid}/mark-paid")]
    public async Task<IActionResult> MarkPaid(
        [FromRoute] Guid orderId,
        [FromBody] MarketplaceMarkPaidRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        var result = await _paymentService.MarkPaidAsync(
            tenantId!,
            clientId,
            orderId,
            request?.Force ?? false,
            cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapValidationError(result.Errors);
        }

        if (result.Data.ConfirmationRequired && result.Data.Confirmation != null)
        {
            return Conflict(CreateApiError(
                "PAYMENT_CONFIRMATION_REQUIRED",
                "Pagamento apos prazo/corte. Confirmacao obrigatoria.",
                result.Data.Confirmation));
        }

        return Ok(result.Data.Result);
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
        {
            return BadRequest(CreateApiError("VALIDATION_ERROR", "Invalid request"));
        }

        if (errors.Any(item => string.Equals(item.Message, "ML_UNMAPPED_ITEM", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("ML_UNMAPPED_ITEM", "Order has unmapped items", errors));
        }

        if (errors.Any(item => string.Equals(item.Field, "orderId", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("ORDER_NOT_FOUND", "Marketplace order not found", errors));
        }

        return BadRequest(CreateApiError("VALIDATION_ERROR", "Invalid request", errors));
    }

    private ApiError CreateApiError(string code, string message, object? errors = null)
    {
        return new ApiError
        {
            Code = code,
            Message = message,
            Errors = errors,
            TraceId = HttpContext.TraceIdentifier
        };
    }
}
