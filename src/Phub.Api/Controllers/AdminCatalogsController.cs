using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phub.Api.Models;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Application.Validation;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/tenants/{tenantId}/catalogs")]
public sealed class AdminCatalogsController : ControllerBase
{
    private readonly AdminCatalogService _adminCatalogService;

    public AdminCatalogsController(AdminCatalogService adminCatalogService)
    {
        _adminCatalogService = adminCatalogService;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromRoute] string tenantId,
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        [FromQuery] bool? isActive = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminCatalogService.ListAsync(tenantId, skip, limit, search, isActive, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpGet("{catalogId:guid}")]
    public async Task<IActionResult> GetById(
        [FromRoute] string tenantId,
        [FromRoute] Guid catalogId,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminCatalogService.GetByIdAsync(tenantId, catalogId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromRoute] string tenantId,
        [FromBody] AdminCatalogUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _adminCatalogService.CreateAsync(tenantId, request, actorId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return StatusCode(StatusCodes.Status201Created, result.Data);
    }

    [HttpPut("{catalogId:guid}")]
    public async Task<IActionResult> Update(
        [FromRoute] string tenantId,
        [FromRoute] Guid catalogId,
        [FromBody] AdminCatalogUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _adminCatalogService.UpdateAsync(tenantId, catalogId, request, actorId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpDelete("{catalogId:guid}")]
    public async Task<IActionResult> Delete(
        [FromRoute] string tenantId,
        [FromRoute] Guid catalogId,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _adminCatalogService.DeactivateAsync(tenantId, catalogId, actorId, cancellationToken);
        if (!result.Succeeded)
        {
            return MapError(result.Errors);
        }

        return NoContent();
    }

    [HttpPut("{catalogId:guid}/products")]
    public async Task<IActionResult> ReplaceProducts(
        [FromRoute] string tenantId,
        [FromRoute] Guid catalogId,
        [FromBody] CatalogReplaceProductsRequest request,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _adminCatalogService.ReplaceProductsAsync(tenantId, catalogId, request, actorId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpPut("{catalogId:guid}/plans")]
    public async Task<IActionResult> ReplacePlans(
        [FromRoute] string tenantId,
        [FromRoute] Guid catalogId,
        [FromBody] CatalogReplacePlansRequest request,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _adminCatalogService.ReplacePlansAsync(tenantId, catalogId, request, actorId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(result.Data);
    }

    private IActionResult MapError(IReadOnlyCollection<ValidationError> errors)
    {
        if (errors.Count == 0)
        {
            return UnprocessableEntity(CreateApiError("VALIDATION_ERROR", "Invalid request"));
        }

        var errorDetails = AdminApiErrorDetailsBuilder.Build(errors);

        if (errors.Any(error => string.Equals(error.Field, "tenantStatus", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("TENANT_INACTIVE", "Tenant inactive", errorDetails));
        }

        if (errors.Any(error => string.Equals(error.Field, "tenantId", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("TENANT_NOT_FOUND", "Tenant not found", errorDetails));
        }

        if (errors.Any(error => string.Equals(error.Field, "catalogId", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("CATALOG_NOT_FOUND", "Catalog not found", errorDetails));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "productSkus", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "invalidSkus", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("INVALID_PRODUCT_SKU", "Invalid product SKUs", errorDetails));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "planIds", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "invalidPlanIds", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("INVALID_PLAN_IDS", "Invalid plan ids", errorDetails));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "name", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "description", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("VALIDATION_ERROR", "Invalid catalog payload", errorDetails));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "skip", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "limit", StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest(CreateApiError("VALIDATION_ERROR", "Invalid pagination query", errorDetails));
        }

        return UnprocessableEntity(CreateApiError("VALIDATION_ERROR", "Invalid request", errorDetails));
    }

    private Guid ResolveActorId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(claim, out var actorId) ? actorId : Guid.Empty;
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
