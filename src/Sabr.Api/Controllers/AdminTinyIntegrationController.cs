using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Services;
using Sabr.Application.Validation;

namespace Sabr.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/clients/{clientId:guid}/integrations/tinyerp")]
public sealed class AdminTinyIntegrationController : ControllerBase
{
    private readonly ILogger<AdminTinyIntegrationController> _logger;
    private readonly ITenantProvider _tenantProvider;
    private readonly TinyIntegrationService _integrationService;

    public AdminTinyIntegrationController(
        ILogger<AdminTinyIntegrationController> logger,
        ITenantProvider tenantProvider,
        TinyIntegrationService integrationService)
    {
        _logger = logger;
        _tenantProvider = tenantProvider;
        _integrationService = integrationService;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus([FromRoute] Guid clientId, CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(out var tenantId, out var error))
        {
            return error!;
        }

        try
        {
            var result = await _integrationService.GetClientStatusAsync(tenantId!, clientId, cancellationToken);
            if (!result.Succeeded || result.Data == null)
            {
                return MapValidationError(result.Errors);
            }

            return Ok(result.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Admin: failed to load Tiny ERP status. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId,
                clientId,
                HttpContext.TraceIdentifier);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                CreateApiError("TINY_STATUS_INTERNAL_ERROR", "Falha interna ao carregar status da integracao Tiny ERP"));
        }
    }

    [HttpPost("sync-orders")]
    public async Task<IActionResult> SyncOrders([FromRoute] Guid clientId, CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(out var tenantId, out var error))
        {
            return error!;
        }

        var result = await _integrationService.SyncOrdersAsync(tenantId!, clientId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapValidationError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpPost("sync-catalog")]
    public async Task<IActionResult> SyncProductCatalog([FromRoute] Guid clientId, CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(out var tenantId, out var error))
        {
            return error!;
        }

        var result = await _integrationService.SyncProductCatalogAsync(tenantId!, clientId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapValidationError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpPost("orders/{orderId:guid}/generate-invoice")]
    public async Task<IActionResult> GenerateInvoice(
        [FromRoute] Guid clientId,
        [FromRoute] Guid orderId,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(out var tenantId, out var error))
        {
            return error!;
        }

        var result = await _integrationService.GenerateInvoiceAsync(tenantId!, clientId, orderId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapValidationError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpGet("orders/{orderId:guid}/invoice-xml")]
    public async Task<IActionResult> GetInvoiceXml(
        [FromRoute] Guid clientId,
        [FromRoute] Guid orderId,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(out var tenantId, out var error))
        {
            return error!;
        }

        // Generate invoice to get the note ID, then fetch XML bytes
        var invoiceResult = await _integrationService.GenerateInvoiceAsync(tenantId!, clientId, orderId, cancellationToken);
        if (!invoiceResult.Succeeded || invoiceResult.Data == null)
        {
            return MapValidationError(invoiceResult.Errors);
        }

        // If the invoice already has a direct XML URL, redirect
        if (!string.IsNullOrWhiteSpace(invoiceResult.Data.XmlUrl))
        {
            return Redirect(invoiceResult.Data.XmlUrl);
        }

        return NotFound(CreateApiError("TINY_INVOICE_XML_NOT_AVAILABLE", "XML da nota fiscal nao disponivel"));
    }

    [HttpGet("orders/{orderId:guid}/invoice-link")]
    public async Task<IActionResult> GetInvoiceLink(
        [FromRoute] Guid clientId,
        [FromRoute] Guid orderId,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(out var tenantId, out var error))
        {
            return error!;
        }

        var invoiceResult = await _integrationService.GenerateInvoiceAsync(tenantId!, clientId, orderId, cancellationToken);
        if (!invoiceResult.Succeeded || invoiceResult.Data == null)
        {
            return MapValidationError(invoiceResult.Errors);
        }

        return Ok(new
        {
            noteId = invoiceResult.Data.NoteId,
            numero = invoiceResult.Data.Numero,
            chaveAcesso = invoiceResult.Data.ChaveAcesso,
            xmlUrl = invoiceResult.Data.XmlUrl,
            danfeUrl = invoiceResult.Data.DanfeUrl,
            situacao = invoiceResult.Data.Situacao
        });
    }

    private bool TryGetTenantId(out string? tenantId, out IActionResult? errorResult)
    {
        tenantId = _tenantProvider.TenantId;
        errorResult = null;

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            errorResult = BadRequest(CreateApiError("TENANT_NOT_RESOLVED", "Tenant not resolved"));
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

        if (errors.Any(e => string.Equals(e.Field, "orderId", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("ORDER_NOT_FOUND", "Order not found", errors));
        }

        if (errors.Any(e => string.Equals(e.Field, "connection", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("TINY_NOT_CONNECTED", "Tiny ERP integration not found", errors));
        }

        if (errors.Any(e => string.Equals(e.Message, "TINY_AUTH_INVALID", StringComparison.OrdinalIgnoreCase)))
        {
            return StatusCode(StatusCodes.Status401Unauthorized,
                CreateApiError("TINY_AUTH_INVALID", "Tiny ERP token is invalid or expired", errors));
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
