using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Sabr.Application.Models;
using Sabr.Application.Services;
using Sabr.Application.Validation;

namespace Sabr.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/products")]
public sealed class AdminProductsController : ControllerBase
{
    private readonly ProductAdminService _productAdminService;

    public AdminProductsController(ProductAdminService productAdminService)
    {
        _productAdminService = productAdminService;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        [FromQuery] bool? isActive = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _productAdminService.ListAsync(skip, limit, search, isActive, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpGet("{sku}")]
    public async Task<IActionResult> GetBySku([FromRoute] string sku, CancellationToken cancellationToken = default)
    {
        var result = await _productAdminService.GetBySkuAsync(sku, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpPost]
    public async Task<IActionResult> Upsert(
        [FromBody] AdminProductUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _productAdminService.UpsertProductAsync(request, actorId, "platform", cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpPut("{sku}")]
    public async Task<IActionResult> Update(
        [FromRoute] string sku,
        [FromBody] JsonElement requestBody,
        CancellationToken cancellationToken)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var request = requestBody.Deserialize<AdminProductUpdateRequest>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (request == null)
        {
            return BadRequest(CreateApiError("VALIDATION_ERROR", "Invalid request payload"));
        }

        request.CategoryIdProvided = requestBody.TryGetProperty("categoryId", out _);

        var result = await _productAdminService.PatchBySkuAsync(sku, request, actorId, "platform", cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpDelete("{sku}")]
    public async Task<IActionResult> Delete([FromRoute] string sku, CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _productAdminService.DeactivateAsync(sku, actorId, "platform", cancellationToken);
        if (!result.Succeeded)
        {
            return MapError(result.Errors);
        }

        return NoContent();
    }

    [HttpPut("{sku}/pricing")]
    public async Task<IActionResult> UpdatePricing(
        [FromRoute] string sku,
        [FromBody] AdminProductPricingUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _productAdminService.UpdatePricingAsync(sku, request, actorId, "platform", cancellationToken);
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

        if (errors.Any(error => string.Equals(error.Field, "sku", StringComparison.OrdinalIgnoreCase) &&
                                error.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("PRODUCT_NOT_FOUND", "Product not found", errors));
        }

        if (errors.Any(error => string.Equals(error.Field, "catalogLinks", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("PRODUCT_MISSING_CATALOG_LINKS", "Active product must be linked to at least one catalog", errors));
        }

        if (errors.Any(error => string.Equals(error.Field, "anatelHomologationNumber", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("ANATEL_REQUIRED", "ANATEL homologation is required and must be valid", errors));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "categoryId", StringComparison.OrdinalIgnoreCase) &&
                error.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("CATEGORY_NOT_FOUND", "Category not found", errors));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "categoryId", StringComparison.OrdinalIgnoreCase) &&
                error.Message.Contains("inactive", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("CATEGORY_INACTIVE", "Category inactive", errors));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "tenantId", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "tenantStatus", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("VALIDATION_ERROR", "Tenant context is required for active products", errors));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "sku", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "name", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "brand", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "ncm", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "ean", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "categoryId", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "description", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "widthCm", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "heightCm", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "lengthCm", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "weightKg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "costPriceCents", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "catalogPriceCents", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "pricing", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("VALIDATION_ERROR", "Invalid product payload", errors));
        }

        if (errors.Any(error => string.Equals(error.Field, "skip", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(error.Field, "limit", StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest(CreateApiError("VALIDATION_ERROR", "Invalid pagination query", errors));
        }

        return BadRequest(CreateApiError("VALIDATION_ERROR", "Invalid request", errors));
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
