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
[Route("api/v1/catalog/products")]
public sealed class CatalogController : ControllerBase
{
    private readonly ITenantProvider _tenantProvider;
    private readonly CatalogService _catalogService;

    public CatalogController(ITenantProvider tenantProvider, CatalogService catalogService)
    {
        _tenantProvider = tenantProvider;
        _catalogService = catalogService;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var errorResult))
        {
            return errorResult!;
        }

        var paginationErrors = PaginationGuard.ValidateOrError(skip, limit);
        if (paginationErrors.Count > 0)
        {
            return BadRequest(CreateApiError("VALIDATION_ERROR", "Invalid pagination query", paginationErrors));
        }

        var result = await _catalogService.GetProductsAsync(tenantId!, clientId, skip, limit, search, cancellationToken);
        return Ok(result);
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
