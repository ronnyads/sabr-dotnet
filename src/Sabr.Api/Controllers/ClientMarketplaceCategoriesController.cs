using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Application.Validation;
using Phub.Domain.Enums;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/client/marketplaces/categories")]
public sealed class ClientMarketplaceCategoriesController : ControllerBase
{
    private readonly ITenantProvider _tenantProvider;
    private readonly IListingCategoryService _listingCategoryService;
    private readonly ILogger<ClientMarketplaceCategoriesController> _logger;

    public ClientMarketplaceCategoriesController(
        ITenantProvider tenantProvider,
        IListingCategoryService listingCategoryService,
        ILogger<ClientMarketplaceCategoriesController> logger)
    {
        _tenantProvider = tenantProvider;
        _listingCategoryService = listingCategoryService;
        _logger = logger;
    }

    [HttpPost("attributes")]
    public async Task<IActionResult> Attributes(
        [FromBody] MarketplaceCategoryAttributesRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        try
        {
            var result = await _listingCategoryService.GetCategoryAttributesAsync(
                tenantId!,
                clientId,
                request ?? new MarketplaceCategoryAttributesRequest(),
                cancellationToken,
                HttpContext.TraceIdentifier);
            if (!result.Succeeded || result.Data == null)
            {
                return MapValidationError(result.Errors);
            }

            return Ok(result.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "category_attributes_unhandled_exception tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId,
                clientId,
                HttpContext.TraceIdentifier);
            return StatusCode(503, CreateApiError("ML_UNAVAILABLE", "Marketplace category attributes unavailable."));
        }
    }

    [HttpPost("suggest")]
    public async Task<IActionResult> Suggest(
        [FromBody] MarketplaceCategorySuggestRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        try
        {
            var result = await _listingCategoryService.SuggestCategoriesAsync(
                tenantId!,
                clientId,
                request ?? new MarketplaceCategorySuggestRequest(),
                cancellationToken);
            if (!result.Succeeded || result.Data == null)
            {
                return MapValidationError(result.Errors);
            }

            if (result.Data.Degraded)
            {
                result.Data.TraceId = HttpContext.TraceIdentifier;
            }

            return Ok(result.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "suggest_unhandled_exception tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId,
                clientId,
                HttpContext.TraceIdentifier);
            return Ok(new MarketplaceCategorySuggestResult
            {
                Items = new List<MarketplaceCategorySuggestItemResult>(),
                Degraded = true,
                Reason = SuggestDegradedReason.ML_UNAVAILABLE,
                TraceId = HttpContext.TraceIdentifier
            });
        }
    }

    private bool TryGetClientContext(out string? tenantId, out Guid clientId, out IActionResult? errorResult)
    {
        tenantId = _tenantProvider.TenantId;
        clientId = Guid.Empty;
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

        return true;
    }

    private IActionResult MapValidationError(IReadOnlyCollection<ValidationError> errors)
    {
        if (errors.Count == 0)
        {
            return BadRequest(CreateApiError("VALIDATION_ERROR", "Invalid request"));
        }

        var code = errors.First().Message;
        var message = code switch
        {
            "CHANNEL_INVALID" => "Only channel mercadolivre is supported in this phase.",
            "INTEGRATION_REQUIRED" => "IntegrationId is required.",
            "CATEGORY_REQUIRED" => "CategoryId is required.",
            "SELLER_INVALID" => "SellerId must be numeric.",
            "INVALID_SELLER_INTEGRATION" => "Seller does not match selected integration.",
            "QUERY_REQUIRED" => "Query is required.",
            "ML_AUTH_INVALID" => "Mercado Livre authentication is invalid. Reconnect integration.",
            "ML_CATEGORY_INVALID" => "CategoryId invalido no Mercado Livre.",
            "ML_UNAVAILABLE" => "Marketplace category attributes unavailable.",
            _ => "Invalid request."
        };

        return code switch
        {
            "CHANNEL_INVALID" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "INTEGRATION_REQUIRED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "CATEGORY_REQUIRED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "SELLER_INVALID" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "INVALID_SELLER_INTEGRATION" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "QUERY_REQUIRED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "ML_AUTH_INVALID" => Unauthorized(CreateApiError(code, message, errors)),
            "ML_CATEGORY_INVALID" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "ML_UNAVAILABLE" => StatusCode(503, CreateApiError(code, message, errors)),
            _ => BadRequest(CreateApiError(code, message, errors))
        };
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
