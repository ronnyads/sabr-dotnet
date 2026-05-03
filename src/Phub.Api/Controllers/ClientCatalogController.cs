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
[Route("api/v1/client/catalog/variants")]
public sealed class ClientCatalogController : ControllerBase
{
    private readonly ITenantProvider _tenantProvider;
    private readonly CatalogSnapshotService _catalogSnapshotService;

    public ClientCatalogController(
        ITenantProvider tenantProvider,
        CatalogSnapshotService catalogSnapshotService)
    {
        _tenantProvider = tenantProvider;
        _catalogSnapshotService = catalogSnapshotService;
    }

    [HttpPost("snapshot")]
    public async Task<IActionResult> Snapshot(
        [FromBody] CatalogVariantSnapshotRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var actorId, out var error))
        {
            return error!;
        }

        var result = await _catalogSnapshotService.GetVariantSnapshotAsync(
            tenantId!,
            clientId,
            actorId,
            request ?? new CatalogVariantSnapshotRequest(),
            cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapValidationError(result.Errors);
        }

        return Ok(result.Data);
    }

    private bool TryGetClientContext(
        out string? tenantId,
        out Guid clientId,
        out Guid actorId,
        out IActionResult? errorResult)
    {
        tenantId = _tenantProvider.TenantId;
        clientId = Guid.Empty;
        actorId = Guid.Empty;
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

        var subject = User.FindFirst("sub")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        actorId = Guid.TryParse(subject, out var parsedActorId) ? parsedActorId : clientId;
        return true;
    }

    private IActionResult MapValidationError(IReadOnlyCollection<ValidationError> errors)
    {
        if (errors.Count == 0)
        {
            return BadRequest(CreateApiError("VALIDATION_ERROR", "Invalid request"));
        }

        var code = errors.First().Message;
        var message = code switch
        {
            "VARIANT_SKU_REQUIRED" => "variantSku is required.",
            "PRODUCT_NOT_FOUND" => "Product not found for this SKU.",
            "SKU_NOT_AUTHORIZED" => "SKU is not authorized for this client.",
            _ => "Invalid request."
        };

        return code switch
        {
            "VARIANT_SKU_REQUIRED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "PRODUCT_NOT_FOUND" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "SKU_NOT_AUTHORIZED" => StatusCode(StatusCodes.Status403Forbidden, CreateApiError(code, message, errors)),
            _ => BadRequest(CreateApiError(code, message, errors))
        };
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
