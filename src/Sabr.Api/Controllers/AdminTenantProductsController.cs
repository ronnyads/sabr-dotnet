using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sabr.Api.Models;
using Sabr.Application.Models;
using Sabr.Application.Services;
using Sabr.Application.Validation;

namespace Sabr.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/tenants/{tenantId}/products")]
public sealed class AdminTenantProductsController : ControllerBase
{
    private readonly ProductAdminService _productAdminService;

    public AdminTenantProductsController(ProductAdminService productAdminService)
    {
        _productAdminService = productAdminService;
    }

    [HttpGet("{sku}/catalogs")]
    public async Task<IActionResult> GetCatalogLinks(
        [FromRoute] string tenantId,
        [FromRoute] string sku,
        CancellationToken cancellationToken = default)
    {
        var result = await _productAdminService.GetLinkedCatalogIdsAsync(tenantId, sku, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(new ProductCatalogLinksResult
        {
            ProductSku = Sabr.Domain.ValueObjects.Sku.Normalize(sku),
            CatalogIds = result.Data
        });
    }

    [HttpPut("{sku}/catalogs")]
    public async Task<IActionResult> ReplaceCatalogLinks(
        [FromRoute] string tenantId,
        [FromRoute] string sku,
        [FromBody] ProductReplaceCatalogsRequest request,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _productAdminService.ReplaceCatalogsAsync(tenantId, sku, request, actorId, cancellationToken);
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

        if (errors.Any(error =>
                string.Equals(error.Field, "sku", StringComparison.OrdinalIgnoreCase) &&
                error.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("PRODUCT_NOT_FOUND", "Product not found", errorDetails));
        }

        if (errors.Any(error => string.Equals(error.Field, "catalogLinks", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("PRODUCT_MISSING_CATALOG_LINKS", "Active product must be linked to at least one catalog", errorDetails));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "catalogIds", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "invalidCatalogIds", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("INVALID_CATALOG_IDS", "Invalid catalog ids", errorDetails));
        }

        if (errors.Any(error => string.Equals(error.Field, "sku", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("VALIDATION_ERROR", "Invalid SKU", errorDetails));
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
