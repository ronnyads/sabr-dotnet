using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phub.Api.Models;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Application.Validation;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/tenants/{tenantSlug}/clients/{clientId:guid}/plan-subscriptions")]
public sealed class AdminClientPlanSubscriptionsController : ControllerBase
{
    private readonly AdminClientPlanSubscriptionService _subscriptionService;

    public AdminClientPlanSubscriptionsController(AdminClientPlanSubscriptionService subscriptionService)
    {
        _subscriptionService = subscriptionService;
    }

    [HttpGet]
    public async Task<IActionResult> GetCurrent(
        [FromRoute] string tenantSlug,
        [FromRoute] Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var result = await _subscriptionService.GetCurrentAsync(tenantSlug, clientId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpPut]
    public async Task<IActionResult> ReplaceSet(
        [FromRoute] string tenantSlug,
        [FromRoute] Guid clientId,
        [FromBody] ClientPlanSubscriptionsReplaceRequest request,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _subscriptionService.ReplaceSetAsync(
            tenantSlug,
            clientId,
            request?.PlanIds,
            actorId,
            cancellationToken);

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

        if (errors.Any(error => string.Equals(error.Field, "clientId", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("CLIENT_NOT_FOUND", "Client not found", errorDetails));
        }

        if (errors.Any(error => string.Equals(error.Field, "inactivePlanIds", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("PLAN_INACTIVE", "One or more selected plans are inactive", errorDetails));
        }

        if (errors.Any(error => string.Equals(error.Field, "invalidPlanIds", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("INVALID_PLAN_IDS", "One or more selected plans are invalid", errorDetails));
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
