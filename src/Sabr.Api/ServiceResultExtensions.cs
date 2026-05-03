using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Phub.Application.Models;
using Phub.Application.Validation;

namespace Phub.Api;

/// <summary>
/// Extensões para mapear <see cref="ServiceResult{T}"/> em <see cref="IActionResult"/> HTTP.
/// Centraliza o mapeamento ErrorCode → HTTP status code, eliminando if/switch repetidos nos controllers.
/// </summary>
public static class ServiceResultExtensions
{
    /// <summary>
    /// Converte <see cref="ServiceResult{T}"/> para <see cref="IActionResult"/>.
    /// </summary>
    /// <param name="result">Resultado do serviço.</param>
    /// <param name="traceId">Correlation ID da request atual.</param>
    /// <param name="successStatus">Status HTTP para sucesso (padrão 200).</param>
    public static IActionResult ToActionResult<T>(
        this ServiceResult<T> result,
        string? traceId = null,
        int successStatus = StatusCodes.Status200OK)
    {
        if (result.Succeeded)
        {
            return successStatus switch
            {
                StatusCodes.Status201Created => new ObjectResult(result.Data) { StatusCode = 201 },
                StatusCodes.Status204NoContent => new NoContentResult(),
                _ => new OkObjectResult(result.Data)
            };
        }

        var (httpStatus, code) = MapErrorCode(result.ErrorCode, result.Errors);

        var error = new ApiError
        {
            Code     = code,
            Message  = result.Errors.FirstOrDefault()?.Message ?? code,
            Errors   = result.Errors.Count > 1 ? result.Errors : null,
            TraceId  = traceId ?? string.Empty
        };

        return new ObjectResult(error) { StatusCode = httpStatus };
    }

    private static (int httpStatus, string code) MapErrorCode(
        string? errorCode,
        IReadOnlyList<ValidationError> errors)
    {
        return errorCode switch
        {
            ServiceErrorCodes.NotFound           => (404, errorCode),
            ServiceErrorCodes.Forbidden          => (403, errorCode),
            ServiceErrorCodes.ConcurrencyConflict => (409, errorCode),
            ServiceErrorCodes.PreconditionRequired => (428, errorCode),
            ServiceErrorCodes.SkuNotAuthorized   => (403, errorCode),
            ServiceErrorCodes.DraftNotFound      => (404, errorCode),
            ServiceErrorCodes.PricingInvalid     => (422, errorCode),
            ServiceErrorCodes.CategoryNotFound   => (422, errorCode),
            ServiceErrorCodes.CategoryInactive   => (422, errorCode),
            ServiceErrorCodes.CategoryHasActiveChildren => (422, errorCode),
            ServiceErrorCodes.CategoryCycleDetected => (422, errorCode),
            ServiceErrorCodes.VariantAlreadyExists => (409, errorCode),
            ServiceErrorCodes.AnatelRequired     => (422, errorCode),
            ServiceErrorCodes.InvalidImageType   => (422, errorCode),
            ServiceErrorCodes.ImageLimitExceeded => (422, errorCode),
            ServiceErrorCodes.ProductMissingCatalogLinks => (422, errorCode),
            ServiceErrorCodes.MlAuthInvalid      => (401, errorCode),
            _ => (422, errors.Count > 0 ? ServiceErrorCodes.ValidationError : "INTERNAL_ERROR")
        };
    }
}
