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
[Route("api/v1/client/listings")]
public sealed class ClientListingDraftsController : ControllerBase
{
    private readonly ITenantProvider _tenantProvider;
    private readonly ListingDraftService _listingDraftService;
    private readonly ILogger<ClientListingDraftsController> _logger;

    public ClientListingDraftsController(
        ITenantProvider tenantProvider,
        ListingDraftService listingDraftService,
        ILogger<ClientListingDraftsController> logger)
    {
        _tenantProvider = tenantProvider;
        _listingDraftService = listingDraftService;
        _logger = logger;
    }

    [HttpPost("drafts/upsert")]
    public async Task<IActionResult> UpsertDraft(
        [FromBody] ListingDraftUpsertRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        try
        {
            var result = await _listingDraftService.UpsertAsync(
                tenantId!,
                clientId,
                request ?? new ListingDraftUpsertRequest(),
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
                "listing_draft_upsert_failed_unexpected tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId,
                clientId,
                HttpContext.TraceIdentifier);
            return StatusCode(503, CreateApiError("ML_UNAVAILABLE", "Listing draft service unavailable."));
        }
    }

    [HttpPost("drafts/get")]
    public async Task<IActionResult> GetDraft(
        [FromBody] ListingDraftGetRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        try
        {
            var result = await _listingDraftService.GetAsync(
                tenantId!,
                clientId,
                request ?? new ListingDraftGetRequest(),
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
                "listing_drafts_get_unhandled_exception tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId,
                clientId,
                HttpContext.TraceIdentifier);
            return StatusCode(503, CreateApiError("ML_UNAVAILABLE", "Listing draft service unavailable."));
        }
    }

    [HttpPost("drafts/publish")]
    public async Task<IActionResult> PublishDraft(
        [FromBody] ListingDraftPublishRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        try
        {
            var result = await _listingDraftService.PublishAsync(
                tenantId!,
                clientId,
                request ?? new ListingDraftPublishRequest(),
                cancellationToken);
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
                "listing_drafts_publish_unhandled_exception tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId,
                clientId,
                HttpContext.TraceIdentifier);
            return StatusCode(503, CreateApiError("ML_UNAVAILABLE", "Listing publish unavailable."));
        }
    }

    [HttpPost("drafts/validate")]
    public async Task<IActionResult> ValidateDraft(
        [FromBody] ListingDraftValidateRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        try
        {
            var result = await _listingDraftService.ValidateDraftAsync(
                tenantId!,
                clientId,
                request ?? new ListingDraftValidateRequest(),
                cancellationToken);
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
                "listing_drafts_validate_unhandled_exception tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId,
                clientId,
                HttpContext.TraceIdentifier);
            return StatusCode(503, CreateApiError("ML_UNAVAILABLE", "Listing validate unavailable."));
        }
    }

    [HttpPost("publications/query")]
    public async Task<IActionResult> QueryPublications(
        [FromBody] ListingPublicationsQueryRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        try
        {
            var result = await _listingDraftService.QueryPublicationsAsync(
                tenantId!,
                clientId,
                request ?? new ListingPublicationsQueryRequest(),
                cancellationToken);
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
                "listing_publications_query_unhandled_exception tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId,
                clientId,
                HttpContext.TraceIdentifier);
            return StatusCode(503, CreateApiError("ML_UNAVAILABLE", "Listing query unavailable."));
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
            "INVALID_SELLER_INTEGRATION" => "Seller does not match selected integration.",
            "CATEGORY_REQUIRED" => "CategoryId is required.",
            "LISTING_TYPE_INVALID" => "Listing type must be gold_special or gold_pro.",
            "PRICE_INVALID" => "Price must be greater than zero.",
            "CURRENCY_NOT_SUPPORTED" => "Only BRL is supported in this phase.",
            "TITLE_REQUIRED" => "Title is required to publish.",
            "TITLE_TOO_LONG" => "Title must have at most 60 characters.",
            "PICTURES_REQUIRED" => "At least one product image is required.",
            "IMAGE_POSITION_INVALID" => "Image positions must be sequential (1..N).",
            "GTIN_OR_REASON_REQUIRED" => "Provide GTIN or EMPTY_GTIN_REASON.",
            "GTIN_INVALID" => "GTIN must contain 8 to 14 numeric digits.",
            "NCM_INVALID" => "NCM must contain 8 numeric digits.",
            "SELLER_MISMATCH_FOR_INTEGRATION" => "Seller does not match integration.",
            "PRICE_PER_VARIATION_NOT_SUPPORTED" => "Price per variation is not supported.",
            "ATTRIBUTE_REQUIRED" => "Required category attribute is missing.",
            "ROWVERSION_REQUIRED" => "RowVersion is required.",
            "DRAFT_NOT_VALIDATED" => "Execute Validar antes de Publicar.",
            "DRAFT_CONCURRENCY_CONFLICT" => "Draft was updated by another request.",
            "LISTING_PUBLISH_IN_PROGRESS" => "Listing publish is already in progress.",
            "ML_AUTH_INVALID" => "Mercado Livre authentication is invalid. Reconnect integration.",
            "ML_UNAVAILABLE" => "Mercado Livre is unavailable at the moment.",
            "ML_CATEGORY_INVALID" => "CategoryId is invalid in Mercado Livre.",
            "ML_PUBLISH_INPUT_INVALID" => "Mercado Livre rejected publish payload. Review category, attributes and fiscal data.",
            "ML_MAPPING_CONFLICT" => "Mapping conflict for this listing item.",
            "DRAFT_NOT_FOUND" => "Draft not found.",
            "SELLER_INVALID" => "SellerId must be numeric.",
            "SKU_NOT_FOUND" => "Variant SKU not found.",
            "PRODUCT_NOT_FOUND" => "Product not found.",
            "SABR_VARIANT_SKU_REQUIRED" => "sabrVariantSku is required.",
            "SELECTED_VARIANTS_REQUIRED" => "Select at least one variant for publish.",
            "MIXED_BASE_SKU_NOT_SUPPORTED" => "Selected variants must belong to the same base product.",
            "MULTI_VARIATION_NOT_ALLOWED" => "Selected category does not support multiple variations.",
            "MAX_VARIATIONS_EXCEEDED" => "Selected variants exceed category limit.",
            "VARIATION_AXIS_NOT_ALLOWED" => "One or more variation axes are not allowed for selected category.",
            "GTIN_REASON_CONFLICT" => "When GTIN is provided, EMPTY_GTIN_REASON must be empty.",
            "VARIANT_SKU_REQUIRED" => "variantSku is required.",
            "STATUS_INVALID" => "Invalid publication status filter.",
            "ML_PUBLISH_FAILED" => "Mercado Livre publish failed.",
            _ => "Invalid request."
        };

        return code switch
        {
            "CHANNEL_INVALID" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "ROWVERSION_REQUIRED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "DRAFT_NOT_VALIDATED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "INTEGRATION_REQUIRED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "INVALID_SELLER_INTEGRATION" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "CATEGORY_REQUIRED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "LISTING_TYPE_INVALID" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "PRICE_INVALID" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "CURRENCY_NOT_SUPPORTED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "TITLE_REQUIRED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "TITLE_TOO_LONG" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "PICTURES_REQUIRED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "IMAGE_POSITION_INVALID" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "GTIN_OR_REASON_REQUIRED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "GTIN_INVALID" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "NCM_INVALID" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "GTIN_REASON_CONFLICT" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "SELLER_MISMATCH_FOR_INTEGRATION" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "PRICE_PER_VARIATION_NOT_SUPPORTED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "ATTRIBUTE_REQUIRED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "SELLER_INVALID" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "SKU_NOT_FOUND" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "PRODUCT_NOT_FOUND" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "SABR_VARIANT_SKU_REQUIRED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "VARIANT_SKU_REQUIRED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "SELECTED_VARIANTS_REQUIRED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "MIXED_BASE_SKU_NOT_SUPPORTED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "MULTI_VARIATION_NOT_ALLOWED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "MAX_VARIATIONS_EXCEEDED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "VARIATION_AXIS_NOT_ALLOWED" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "ML_CATEGORY_INVALID" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "ML_PUBLISH_INPUT_INVALID" => UnprocessableEntity(CreateApiError(code, message, errors)),
            "ML_AUTH_INVALID" => Unauthorized(CreateApiError(code, message, errors)),
            "ML_UNAVAILABLE" => StatusCode(503, CreateApiError(code, message, errors)),
            "DRAFT_CONCURRENCY_CONFLICT" => Conflict(CreateApiError(code, message, errors)),
            "LISTING_PUBLISH_IN_PROGRESS" => Conflict(CreateApiError(code, message, errors)),
            "ML_MAPPING_CONFLICT" => Conflict(CreateApiError(code, message, errors)),
            "DRAFT_NOT_FOUND" => NotFound(CreateApiError(code, message, errors)),
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
