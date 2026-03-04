using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sabr.Api.Models;
using Sabr.Application.Models;
using Sabr.Application.Services;
using Sabr.Application.Validation;

namespace Sabr.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/tenants/{tenantId}/plans")]
public sealed class AdminPlansController : ControllerBase
{
    private readonly AdminPlanService _adminPlanService;

    public AdminPlansController(AdminPlanService adminPlanService)
    {
        _adminPlanService = adminPlanService;
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
        var result = await _adminPlanService.ListAsync(tenantId, skip, limit, search, isActive, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpGet("{planId:guid}")]
    public async Task<IActionResult> GetById(
        [FromRoute] string tenantId,
        [FromRoute] Guid planId,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminPlanService.GetByIdAsync(tenantId, planId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromRoute] string tenantId,
        [FromBody] AdminPlanUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _adminPlanService.CreateAsync(tenantId, request, actorId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return StatusCode(StatusCodes.Status201Created, result.Data);
    }

    [HttpPut("{planId:guid}")]
    public async Task<IActionResult> Update(
        [FromRoute] string tenantId,
        [FromRoute] Guid planId,
        [FromBody] AdminPlanUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _adminPlanService.UpdateAsync(tenantId, planId, request, actorId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpDelete("{planId:guid}")]
    public async Task<IActionResult> Delete(
        [FromRoute] string tenantId,
        [FromRoute] Guid planId,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _adminPlanService.DeactivateAsync(tenantId, planId, actorId, cancellationToken);
        if (!result.Succeeded)
        {
            return MapError(result.Errors);
        }

        return NoContent();
    }

    [HttpPut("{planId:guid}/catalogs")]
    public async Task<IActionResult> ReplaceCatalogs(
        [FromRoute] string tenantId,
        [FromRoute] Guid planId,
        [FromBody] PlanReplaceCatalogsRequest request,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _adminPlanService.ReplaceCatalogsAsync(tenantId, planId, request, actorId, cancellationToken);
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

        if (errors.Any(error => string.Equals(error.Field, "planId", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("PLAN_NOT_FOUND", "Plan not found", errorDetails));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "catalogIds", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "invalidCatalogIds", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("INVALID_CATALOG_IDS", "Invalid catalog ids", errorDetails));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "name", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "isActive", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "billingPeriod", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("VALIDATION_ERROR", "Invalid plan payload", errorDetails));
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
