using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Application.Validation;
using Phub.Domain.Enums;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/client/marketplace-mappings")]
public sealed class ClientMarketplaceMappingsController : ControllerBase
{
    private readonly ITenantProvider _tenantProvider;
    private readonly CatalogService _catalogService;
    private readonly MarketplaceOrderMappingService _mappingService;

    public ClientMarketplaceMappingsController(
        ITenantProvider tenantProvider,
        CatalogService catalogService,
        MarketplaceOrderMappingService mappingService)
    {
        _tenantProvider = tenantProvider;
        _catalogService = catalogService;
        _mappingService = mappingService;
    }

    [HttpGet("catalog-options")]
    public async Task<IActionResult> CatalogOptions(
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 50,
        [FromQuery] string? search = null,
        [FromQuery] string? productSku = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        var paginationErrors = PaginationGuard.ValidateOrError(skip, limit);
        if (paginationErrors.Count > 0)
        {
            return BadRequest(CreateApiError("VALIDATION_ERROR", "Invalid pagination query", paginationErrors));
        }

        var result = await _catalogService.GetVariantsAsync(
            tenantId!,
            clientId,
            skip,
            limit,
            search,
            productSku,
            cancellationToken);
        return Ok(result);
    }

    [HttpGet]
    public async Task<IActionResult> ListMappings(
        [FromQuery] string provider,
        [FromQuery] string? sellerId = null,
        [FromQuery] Guid? integrationId = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        if (!TryParseProvider(provider, out var providerValue))
        {
            return BadRequest(CreateApiError("INVALID_PROVIDER", "Marketplace provider is invalid."));
        }

        var result = await _mappingService.ListMappingsAsync(
            tenantId!,
            clientId,
            providerValue,
            sellerId,
            integrationId,
            cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapValidationErrors(result.Errors, result.ErrorCode);
        }

        return Ok(result.Data);
    }

    [HttpGet("unmapped-items")]
    public async Task<IActionResult> ListUnmappedItems(
        [FromQuery] string provider,
        [FromQuery] string? sellerId = null,
        [FromQuery] Guid? integrationId = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        if (!TryParseProvider(provider, out var providerValue))
        {
            return BadRequest(CreateApiError("INVALID_PROVIDER", "Marketplace provider is invalid."));
        }

        var result = await _mappingService.ListUnmappedItemsAsync(
            tenantId!,
            clientId,
            providerValue,
            sellerId,
            integrationId,
            cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapValidationErrors(result.Errors, result.ErrorCode);
        }

        return Ok(result.Data);
    }

    [HttpPost]
    public async Task<IActionResult> UpsertMapping(
        [FromBody] MarketplaceUpsertMappingRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        if (request == null)
        {
            return BadRequest(CreateApiError("VALIDATION_ERROR", "Payload is required."));
        }

        var result = await _mappingService.UpsertMappingAsync(
            tenantId!,
            clientId,
            request,
            cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapValidationErrors(result.Errors, result.ErrorCode);
        }

        return Ok(result.Data);
    }

    private bool TryGetClientContext(
        out string? tenantId,
        out Guid clientId,
        out IActionResult? errorResult)
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

    private bool TryParseProvider(string? provider, out MarketplaceProvider providerValue)
    {
        providerValue = MarketplaceProvider.MercadoLivre;
        if (string.IsNullOrWhiteSpace(provider))
        {
            return false;
        }

        if (Enum.TryParse<MarketplaceProvider>(provider, ignoreCase: true, out providerValue))
        {
            return true;
        }

        return int.TryParse(provider, out var providerNumber)
               && Enum.IsDefined(typeof(MarketplaceProvider), providerNumber)
               && Enum.TryParse(providerNumber.ToString(), out providerValue);
    }

    private IActionResult MapValidationErrors(IReadOnlyCollection<ValidationError> errors, string? code)
    {
        var resolvedCode = code ?? errors.FirstOrDefault()?.Message ?? "VALIDATION_ERROR";
        return resolvedCode switch
        {
            ServiceErrorCodes.SkuNotAuthorized => StatusCode(StatusCodes.Status403Forbidden, CreateApiError(resolvedCode, "SKU not authorized", errors)),
            ServiceErrorCodes.NotFound => NotFound(CreateApiError(resolvedCode, "Resource not found", errors)),
            _ => UnprocessableEntity(CreateApiError(resolvedCode, "Validation failed", errors))
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
