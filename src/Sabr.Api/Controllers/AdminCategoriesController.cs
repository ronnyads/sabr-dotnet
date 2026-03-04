using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sabr.Application.Models;
using Sabr.Application.Services;
using Sabr.Application.Validation;

namespace Sabr.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/categories")]
public sealed class AdminCategoriesController : ControllerBase
{
    private readonly AdminCategoryService _adminCategoryService;

    public AdminCategoriesController(AdminCategoryService adminCategoryService)
    {
        _adminCategoryService = adminCategoryService;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        [FromQuery] bool? isActive = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _adminCategoryService.ListAsync(skip, limit, search, isActive, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpGet("tree")]
    public async Task<IActionResult> Tree(CancellationToken cancellationToken = default)
    {
        var result = await _adminCategoryService.TreeAsync(cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpGet("{categoryId:guid}")]
    public async Task<IActionResult> GetById([FromRoute] Guid categoryId, CancellationToken cancellationToken = default)
    {
        var result = await _adminCategoryService.GetByIdAsync(categoryId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AdminCategoryUpsertRequest request, CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _adminCategoryService.CreateAsync(request, actorId, "platform", cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return StatusCode(StatusCodes.Status201Created, result.Data);
    }

    [HttpPut("{categoryId:guid}")]
    public async Task<IActionResult> Update(
        [FromRoute] Guid categoryId,
        [FromBody] AdminCategoryUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _adminCategoryService.UpdateAsync(categoryId, request, actorId, "platform", cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpDelete("{categoryId:guid}")]
    public async Task<IActionResult> Deactivate([FromRoute] Guid categoryId, CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _adminCategoryService.DeactivateAsync(categoryId, actorId, "platform", cancellationToken);
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

        if (errors.Any(error => string.Equals(error.Field, "categoryId", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("CATEGORY_NOT_FOUND", "Category not found", errors));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "parentId", StringComparison.OrdinalIgnoreCase) &&
                error.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("PARENT_CATEGORY_NOT_FOUND", "Parent category not found", errors));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "parentId", StringComparison.OrdinalIgnoreCase) &&
                error.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("CATEGORY_CYCLE_DETECTED", "Category hierarchy contains a cycle", errors));
        }

        if (errors.Any(error => string.Equals(error.Field, "children", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("CATEGORY_HAS_ACTIVE_CHILDREN", "Category has active children", errors));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "slug", StringComparison.OrdinalIgnoreCase) &&
                error.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)))
        {
            return Conflict(CreateApiError("CATEGORY_SLUG_ALREADY_EXISTS", "Category slug already exists", errors));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "skip", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Field, "limit", StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest(CreateApiError("VALIDATION_ERROR", "Invalid pagination query", errors));
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
