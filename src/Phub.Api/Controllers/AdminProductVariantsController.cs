using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Application.Validation;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/products/{sku}/variants")]
public sealed class AdminProductVariantsController : ControllerBase
{
    private readonly ProductVariantService _productVariantService;

    public AdminProductVariantsController(ProductVariantService productVariantService)
    {
        _productVariantService = productVariantService;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromRoute] string sku, CancellationToken cancellationToken = default)
    {
        var result = await _productVariantService.ListAsync(sku, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromRoute] string sku,
        [FromBody] AdminProductVariantCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _productVariantService.CreateAsync(sku, request, actorId, "platform", cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return StatusCode(StatusCodes.Status201Created, result.Data);
    }

    [HttpPut("{variantSku}")]
    public async Task<IActionResult> Update(
        [FromRoute] string sku,
        [FromRoute] string variantSku,
        [FromBody] AdminProductVariantUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _productVariantService.UpdateAsync(sku, variantSku, request, actorId, "platform", cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpDelete("{variantSku}")]
    public async Task<IActionResult> Deactivate(
        [FromRoute] string sku,
        [FromRoute] string variantSku,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _productVariantService.DeactivateAsync(sku, variantSku, actorId, "platform", cancellationToken);
        if (!result.Succeeded)
        {
            return MapError(result.Errors);
        }

        return NoContent();
    }

    private IActionResult MapError(IReadOnlyCollection<ValidationError> errors)
    {
        if (errors.Count == 0)
        {
            return UnprocessableEntity(CreateApiError("VALIDATION_ERROR", "Invalid request"));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "sku", StringComparison.OrdinalIgnoreCase) &&
                error.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("PRODUCT_NOT_FOUND", "Product not found", errors));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "variantSku", StringComparison.OrdinalIgnoreCase) &&
                error.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("VARIANT_NOT_FOUND", "Variant not found", errors));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "variantSku", StringComparison.OrdinalIgnoreCase) &&
                error.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)))
        {
            return Conflict(CreateApiError("VARIANT_ALREADY_EXISTS", "Variant already exists", errors));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "sku", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "variantSku", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "name", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "costPriceCents", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "catalogPriceCents", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "physicalStock", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "reservedStock", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("VALIDATION_ERROR", "Invalid variant payload", errors));
        }

        return UnprocessableEntity(CreateApiError("VALIDATION_ERROR", "Invalid request", errors));
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
