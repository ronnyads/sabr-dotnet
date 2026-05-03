using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phub.Api.Models;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Application.Validation;

namespace Phub.Api.Controllers;

/// <summary>
/// Gerencia catálogos globais da plataforma (sem tenant).
/// Catálogos são recursos da plataforma — Admin cria catálogos,
/// associa a Planos, e clientes com esses planos acessam os catálogos.
/// </summary>
[ApiController]
[Route("api/v1/admin/catalogs")]
[Authorize(Roles = "Admin,SuperAdmin")]
public sealed class AdminGlobalCatalogsController : ControllerBase
{
    private readonly AdminCatalogService _adminCatalogService;

    public AdminGlobalCatalogsController(AdminCatalogService adminCatalogService)
    {
        _adminCatalogService = adminCatalogService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<AdminCatalogGlobalResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListGlobal(
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 50,
        [FromQuery] string? search = null,
        [FromQuery] bool? isActive = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminCatalogService.ListGlobalAsync(skip, limit, search, isActive, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{catalogId:guid}")]
    [ProducesResponseType(typeof(AdminCatalogDetailResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        [FromRoute] Guid catalogId,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminCatalogService.GetByIdAsync(catalogId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return NotFound(CreateApiError("CATALOG_NOT_FOUND", "Catalog not found"));
        }

        return Ok(result.Data);
    }

    [HttpPost]
    [ProducesResponseType(typeof(AdminCatalogDetailResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(
        [FromBody] AdminCatalogUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _adminCatalogService.CreateAsync(request, actorId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return StatusCode(StatusCodes.Status201Created, result.Data);
    }

    [HttpPut("{catalogId:guid}")]
    [ProducesResponseType(typeof(AdminCatalogDetailResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        [FromRoute] Guid catalogId,
        [FromBody] AdminCatalogUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _adminCatalogService.UpdateAsync(catalogId, request, actorId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpDelete("{catalogId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        [FromRoute] Guid catalogId,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _adminCatalogService.DeactivateAsync(catalogId, actorId, cancellationToken);
        if (!result.Succeeded)
        {
            return MapError(result.Errors);
        }

        return NoContent();
    }

    [HttpPut("{catalogId:guid}/products")]
    [ProducesResponseType(typeof(AdminCatalogDetailResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ReplaceProducts(
        [FromRoute] Guid catalogId,
        [FromBody] CatalogReplaceProductsRequest request,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _adminCatalogService.ReplaceProductsAsync(catalogId, request, actorId, cancellationToken);
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
                string.Equals(error.Field, "name", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "description", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("VALIDATION_ERROR", "Invalid catalog payload", errorDetails));
        }

        return UnprocessableEntity(CreateApiError("VALIDATION_ERROR", "Invalid request", errorDetails));
    }

    private Guid ResolveActorId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                    ?? User.FindFirst("sub")?.Value;
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
