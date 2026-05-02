using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Services;
using Sabr.Application.Validation;

namespace Sabr.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/tenants/{tenantSlug}/clients/{clientId:guid}/integrations/mercadolivre")]
public sealed class AdminTenantMercadoLivreController : ControllerBase
{
    private readonly MercadoLivreIntegrationService _integrationService;
    private readonly IAppDbContext _dbContext;
    private readonly MarketplaceShipmentLabelService _shipmentLabelService;

    public AdminTenantMercadoLivreController(
        MercadoLivreIntegrationService integrationService,
        IAppDbContext dbContext,
        MarketplaceShipmentLabelService shipmentLabelService)
    {
        _integrationService = integrationService;
        _dbContext = dbContext;
        _shipmentLabelService = shipmentLabelService;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(
        [FromRoute] string tenantSlug,
        [FromRoute] Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var result = await _integrationService.GetAdminStatusAsync(tenantSlug, clientId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapValidationError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpGet("operations/orders/{orderId:guid}/shipment")]
    public async Task<IActionResult> GetShipmentForOrder(
        [FromRoute] string tenantSlug,
        [FromRoute] Guid clientId,
        [FromRoute] Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var tenant = await _dbContext.Tenants.AsNoTracking().FirstOrDefaultAsync(
            item => item.Slug == tenantSlug.Trim().ToLowerInvariant(),
            cancellationToken);
        if (tenant == null)
        {
            return NotFound(CreateApiError("TENANT_NOT_FOUND", "Tenant not found"));
        }

        var order = await _dbContext.MarketplaceOrders.AsNoTracking().FirstOrDefaultAsync(
            item => item.Id == orderId
                    && item.TenantId == tenant.Id
                    && item.ClientId == clientId,
            cancellationToken);
        if (order == null || string.IsNullOrWhiteSpace(order.ShipmentId))
        {
            return NotFound(CreateApiError("SHIPMENT_NOT_FOUND", "Shipment not found for order"));
        }

        return Ok(new
        {
            orderId = order.Id,
            shipmentId = order.ShipmentId
        });
    }

    [HttpGet("operations/labels/{shipmentId}")]
    public async Task<IActionResult> GetShipmentLabel(
        [FromRoute] string tenantSlug,
        [FromRoute] Guid clientId,
        [FromRoute] string shipmentId,
        CancellationToken cancellationToken = default)
    {
        var tenant = await _dbContext.Tenants.AsNoTracking().FirstOrDefaultAsync(
            item => item.Slug == tenantSlug.Trim().ToLowerInvariant(),
            cancellationToken);
        if (tenant == null)
        {
            return NotFound(CreateApiError("TENANT_NOT_FOUND", "Tenant not found"));
        }

        var result = await _shipmentLabelService.GetOrFetchAsync(tenant.Id, clientId, shipmentId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return NotFound(CreateApiError("LABEL_NOT_FOUND", "Label not found for shipment"));
        }

        return File(result.Data.Content, result.Data.ContentType, result.Data.FileName);
    }

    /// <summary>
    /// Force disconnect a Mercado Livre integration for a client.
    /// Used by admin to manually clean up problematic connections.
    /// </summary>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ForceDisconnect(
        [FromRoute] string tenantSlug,
        [FromRoute] Guid clientId,
        [FromQuery] string? sellerId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _integrationService.DisconnectAsync(tenantSlug, clientId, sellerId, cancellationToken);
        if (!result.Succeeded)
        {
            var message = result.Errors.Count > 0
                ? result.Errors[0].Message
                : "Failed to disconnect integration";
            return NotFound(CreateApiError(result.ErrorCode ?? "DISCONNECT_FAILED", message));
        }

        return NoContent();
    }

    private IActionResult MapValidationError(IReadOnlyCollection<ValidationError> errors)
    {
        if (errors.Count == 0)
        {
            return BadRequest(CreateApiError("VALIDATION_ERROR", "Invalid request"));
        }

        if (errors.Any(item => string.Equals(item.Field, "tenantSlug", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("TENANT_NOT_FOUND", "Tenant not found", errors));
        }

        if (errors.Any(item => string.Equals(item.Field, "clientId", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("CLIENT_NOT_FOUND", "Client not found", errors));
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
