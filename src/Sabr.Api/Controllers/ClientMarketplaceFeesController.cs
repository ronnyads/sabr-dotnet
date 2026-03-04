using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Services;
using Sabr.Application.Validation;
using Sabr.Domain.Enums;

namespace Sabr.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/client/marketplaces/fees")]
public sealed class ClientMarketplaceFeesController : ControllerBase
{
    private readonly ITenantProvider _tenantProvider;
    private readonly ListingDraftService _listingDraftService;
    private readonly ILogger<ClientMarketplaceFeesController> _logger;

    public ClientMarketplaceFeesController(
        ITenantProvider tenantProvider,
        ListingDraftService listingDraftService,
        ILogger<ClientMarketplaceFeesController> logger)
    {
        _tenantProvider = tenantProvider;
        _listingDraftService = listingDraftService;
        _logger = logger;
    }

    [HttpPost("estimate")]
    public async Task<IActionResult> Estimate(
        [FromBody] MarketplaceFeesEstimateRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        try
        {
            var result = await _listingDraftService.EstimateFeesAsync(
                tenantId!,
                clientId,
                request ?? new MarketplaceFeesEstimateRequest(),
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
                "fees_estimate_unhandled_exception tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId,
                clientId,
                HttpContext.TraceIdentifier);
            return StatusCode(503, CreateApiError("ML_UNAVAILABLE", "Marketplace fees estimate unavailable."));
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
            "LISTING_TYPE_INVALID" => "Listing type must be gold_special or gold_pro.",
            "PRICE_INVALID" => "Price must be greater than zero.",
            "CURRENCY_NOT_SUPPORTED" => "Only BRL is supported in this phase.",
            "SELLER_MISMATCH_FOR_INTEGRATION" => "Seller does not match integration.",
            "INVALID_SELLER_INTEGRATION" => "Seller does not match selected integration.",
            "SELLER_INVALID" => "SellerId must be numeric.",
            "ML_AUTH_INVALID" => "Mercado Livre authentication is invalid. Reconnect integration.",
            "ML_FEES_INPUT_INVALID" => "Categoria, tipo de anuncio ou preco invalido no Mercado Livre.",
            "ML_UNAVAILABLE" => "Marketplace fees estimate unavailable.",
            _ => "Invalid request."
        };

        return code switch
        {
            "CHANNEL_INVALID" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "INTEGRATION_REQUIRED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "CATEGORY_REQUIRED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "LISTING_TYPE_INVALID" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "PRICE_INVALID" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "CURRENCY_NOT_SUPPORTED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "SELLER_MISMATCH_FOR_INTEGRATION" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "INVALID_SELLER_INTEGRATION" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "SELLER_INVALID" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "ML_AUTH_INVALID" => Unauthorized(CreateApiError(code, message, errors)),
            "ML_FEES_INPUT_INVALID" => UnprocessableEntity(CreateApiError(code, message, errors)),
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
