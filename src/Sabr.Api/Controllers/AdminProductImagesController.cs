using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sabr.Api.Models;
using Sabr.Application.Models;
using Sabr.Application.Services;
using Sabr.Application.Validation;

namespace Sabr.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/products/{sku}/images")]
public sealed class AdminProductImagesController : ControllerBase
{
    private readonly ProductImagesService _productImagesService;

    public AdminProductImagesController(ProductImagesService productImagesService)
    {
        _productImagesService = productImagesService;
    }

    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(ProductImagesService.MaxImageSizeBytes + 1024)]
    public async Task<IActionResult> Upload(
        [FromRoute] string sku,
        [FromForm] ProductImageUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        if (request.File == null)
        {
            return UnprocessableEntity(CreateApiError("INVALID_IMAGE_TYPE", "Image file is required", new[]
            {
                new ValidationError("file", "Image file is required")
            }));
        }

        await using var stream = request.File.OpenReadStream();
        var result = await _productImagesService.UploadAsync(
            sku,
            request.File.FileName,
            request.File.ContentType,
            stream,
            request.File.Length,
            actorId,
            "platform",
            cancellationToken);

        if (!result.Succeeded || result.Data == null)
        {
            return MapError(result.Errors);
        }

        return StatusCode(StatusCodes.Status201Created, result.Data);
    }

    [HttpDelete("{imageId:guid}")]
    public async Task<IActionResult> Delete(
        [FromRoute] string sku,
        [FromRoute] Guid imageId,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _productImagesService.DeleteAsync(sku, imageId, actorId, "platform", cancellationToken);
        if (!result.Succeeded)
        {
            return MapError(result.Errors);
        }

        return NoContent();
    }

    [HttpPut("{imageId:guid}/primary")]
    public async Task<IActionResult> SetPrimary(
        [FromRoute] string sku,
        [FromRoute] Guid imageId,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActorId();
        if (actorId == Guid.Empty)
        {
            return Unauthorized(CreateApiError("INVALID_ACTOR", "Invalid actor"));
        }

        var result = await _productImagesService.SetPrimaryAsync(sku, imageId, actorId, "platform", cancellationToken);
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

        if (errors.Any(error =>
                string.Equals(error.Field, "sku", StringComparison.OrdinalIgnoreCase) &&
                error.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("PRODUCT_NOT_FOUND", "Product not found", errors));
        }

        if (errors.Any(error => string.Equals(error.Field, "imageId", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("IMAGE_NOT_FOUND", "Image not found", errors));
        }

        if (errors.Any(error => string.Equals(error.Field, "images", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("IMAGE_LIMIT_EXCEEDED", "Image limit exceeded", errors));
        }

        if (errors.Any(error => string.Equals(error.Field, "fileType", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("INVALID_IMAGE_TYPE", "Invalid image type", errors));
        }

        if (errors.Any(error =>
                string.Equals(error.Field, "fileSize", StringComparison.OrdinalIgnoreCase) &&
                error.Message.Contains("exceeds", StringComparison.OrdinalIgnoreCase)))
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, CreateApiError("PAYLOAD_TOO_LARGE", "Image payload is too large", errors));
        }

        if (errors.Any(error => string.Equals(error.Field, "sku", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("VALIDATION_ERROR", "Invalid SKU", errors));
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
