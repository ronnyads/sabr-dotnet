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
[Route("api/v1/my-products")]
public sealed class MyProductsController : ControllerBase
{
    private readonly ITenantProvider _tenantProvider;
    private readonly MyProductsService _myProductsService;

    public MyProductsController(ITenantProvider tenantProvider, MyProductsService myProductsService)
    {
        _tenantProvider = tenantProvider;
        _myProductsService = myProductsService;
    }

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddMyProductRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var userId, out var errorResult))
        {
            return errorResult!;
        }

        var idempotencyKey = Request.Headers.TryGetValue("Idempotency-Key", out var key) ? key.ToString() : null;

        var result = await _myProductsService.AddToMyProductsAsync(
            request,
            tenantId!,
            clientId,
            userId,
            idempotencyKey,
            cancellationToken);

        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        SetDraftEtag(result.Data.Draft.RowVersion);
        if (result.Data.Created)
        {
            return StatusCode(StatusCodes.Status201Created, result.Data.Draft);
        }

        return Ok(result.Data.Draft);
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? variantSku = null,
        [FromQuery(Name = "variantSkus")] string[]? variantSkus = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out _, out var errorResult))
        {
            return errorResult!;
        }

        var paginationErrors = PaginationGuard.ValidateOrError(skip, limit);
        if (paginationErrors.Count > 0)
        {
            return BadRequest(CreateApiError("VALIDATION_ERROR", "Invalid pagination query", paginationErrors));
        }

        var items = await _myProductsService.ListMyProductsAsync(
            tenantId!,
            clientId,
            skip,
            limit,
            search,
            variantSku,
            variantSkus,
            cancellationToken);
        return Ok(items);
    }

    [HttpGet("{draftId:guid}")]
    public async Task<IActionResult> GetById(Guid draftId, CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out _, out var errorResult))
        {
            return errorResult!;
        }

        var draft = await _myProductsService.GetMyProductDraftByIdAsync(draftId, tenantId!, clientId, cancellationToken);
        if (draft == null)
        {
            return NotFound(CreateApiError("DRAFT_NOT_FOUND", "Draft not found"));
        }

        SetDraftEtag(draft.RowVersion);
        return Ok(draft);
    }

    [HttpPut("{draftId:guid}")]
    public async Task<IActionResult> Update(
        Guid draftId,
        [FromBody] UpdateMyProductDraftRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var userId, out var errorResult))
        {
            return errorResult!;
        }

        var ifMatch = Request.Headers.TryGetValue("If-Match", out var ifMatchHeader) ? ifMatchHeader.ToString() : null;
        var result = await _myProductsService.UpdateMyProductDraftAsync(
            draftId,
            request,
            tenantId!,
            clientId,
            userId,
            ifMatch,
            cancellationToken);

        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        SetDraftEtag(result.Data.RowVersion);
        return Ok(result.Data);
    }

    [HttpDelete("{draftId:guid}")]
    public async Task<IActionResult> Delete(Guid draftId, CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var userId, out var errorResult))
        {
            return errorResult!;
        }

        await _myProductsService.RemoveMyProductDraftAsync(draftId, tenantId!, clientId, userId, cancellationToken);
        return NoContent();
    }

    private IActionResult MapError(IReadOnlyCollection<ValidationError> errors)
    {
        if (errors.Count == 0)
        {
            return BadRequest(CreateApiError("VALIDATION_ERROR", "Invalid request"));
        }

        if (errors.Any(error => string.Equals(error.Field, "productSku", StringComparison.OrdinalIgnoreCase) &&
                                error.Message.Contains("not available", StringComparison.OrdinalIgnoreCase)))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                CreateApiError("SKU_NOT_AUTHORIZED", "SKU is not authorized for this client", errors));
        }

        if (errors.Any(error => string.Equals(error.Field, "draftId", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("DRAFT_NOT_FOUND", "Draft not found", errors));
        }

        if (errors.Any(error => string.Equals(error.Field, "precondition", StringComparison.OrdinalIgnoreCase)))
        {
            return StatusCode(
                StatusCodes.Status428PreconditionRequired,
                CreateApiError(
                    "PRECONDITION_REQUIRED",
                    "Recarregue o draft e tente novamente com If-Match ou rowVersion.",
                    errors));
        }

        if (errors.Any(error => string.Equals(error.Field, "concurrency", StringComparison.OrdinalIgnoreCase)))
        {
            return Conflict(CreateApiError("CONCURRENCY_CONFLICT", "Draft was changed by another request", errors));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "pricingMode", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "markupPercent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "fixedPriceCents", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "baseCatalogCents", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("PRICING_INVALID", "Pricing configuration is invalid", errors));
        }

        if (errors.Any(error => string.Equals(error.Field, "idempotency", StringComparison.OrdinalIgnoreCase)))
        {
            return Conflict(CreateApiError("IDEMPOTENCY_CONFLICT", "Idempotency key conflict", errors));
        }

        return BadRequest(CreateApiError("VALIDATION_ERROR", "Invalid request", errors));
    }

    private bool TryGetClientContext(out string? tenantId, out Guid clientId, out Guid userId, out IActionResult? errorResult)
    {
        tenantId = _tenantProvider.TenantId;
        clientId = Guid.Empty;
        userId = Guid.Empty;
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
        userId = Guid.TryParse(subject, out var parsedUserId) ? parsedUserId : clientId;
        return true;
    }

    private void SetDraftEtag(string rowVersion)
    {
        Response.Headers.ETag = $"\"{rowVersion}\"";
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
