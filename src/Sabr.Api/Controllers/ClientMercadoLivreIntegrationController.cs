using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Sabr.Api.Security;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Options;
using Sabr.Application.Services;
using Sabr.Application.Validation;
using Sabr.Domain.Enums;

namespace Sabr.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/client/integrations/mercadolivre")]
public sealed class ClientMercadoLivreIntegrationController : ControllerBase
{
    private readonly ILogger<ClientMercadoLivreIntegrationController> _logger;
    private readonly ITenantProvider _tenantProvider;
    private readonly MercadoLivreOAuthStateService _oauthStateService;
    private readonly MercadoLivreOAuthService _oauthService;
    private readonly MercadoLivreIntegrationService _integrationService;
    private readonly MercadoLivreSyncService _syncService;
    private readonly MercadoLivreMappingService _mappingService;
    private readonly MercadoLivrePublishValidationService _publishValidationService;
    private readonly MercadoLivrePublishService _publishService;
    private readonly MercadoLivreOptions _mercadoLivreOptions;

    public ClientMercadoLivreIntegrationController(
        ILogger<ClientMercadoLivreIntegrationController> logger,
        ITenantProvider tenantProvider,
        MercadoLivreOAuthStateService oauthStateService,
        MercadoLivreOAuthService oauthService,
        MercadoLivreIntegrationService integrationService,
        MercadoLivreSyncService syncService,
        MercadoLivreMappingService mappingService,
        MercadoLivrePublishValidationService publishValidationService,
        MercadoLivrePublishService publishService,
        IOptions<MercadoLivreOptions> mercadoLivreOptions)
    {
        _logger = logger;
        _tenantProvider = tenantProvider;
        _oauthStateService = oauthStateService;
        _oauthService = oauthService;
        _integrationService = integrationService;
        _syncService = syncService;
        _mappingService = mappingService;
        _publishValidationService = publishValidationService;
        _publishService = publishService;
        _mercadoLivreOptions = mercadoLivreOptions.Value;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        try
        {
            var status = await _integrationService.GetClientStatusAsync(tenantId!, clientId, cancellationToken);
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to load MercadoLivre status. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId,
                clientId,
                HttpContext.TraceIdentifier);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                CreateApiError("ML_STATUS_INTERNAL_ERROR", "Falha interna ao carregar status da integracao Mercado Livre"));
        }
    }

    [HttpPost("connect-url")]
    public IActionResult CreateConnectUrl([FromBody] MercadoLivreConnectUrlRequest? request)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        if (!_oauthService.IsAppConfigured(out var configurationMessage))
        {
            return BadRequest(CreateApiError("ML_APP_NOT_CONFIGURED", configurationMessage));
        }

        var state = _oauthStateService.CreateState(tenantId!, clientId, request?.ReturnUrl);
        var connectUrl = _oauthService.BuildConnectUrl(state);
        return Ok(new MercadoLivreConnectUrlResult
        {
            Url = connectUrl
        });
    }

    [AllowAnonymous]
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return Redirect(BuildClientRedirectTarget("/client/integrations/mercadolivre?ml=missing_code_or_state"));
        }

        if (!_oauthStateService.TryReadState(state, out var payload))
        {
            return Redirect(BuildClientRedirectTarget("/client/integrations/mercadolivre?ml=invalid_state"));
        }

        var target = payload.ReturnUrl;
        try
        {
            var result = await _oauthService.HandleCallbackAsync(payload.TenantId, payload.ClientId, code, cancellationToken);
            if (!result.Succeeded || result.Data == null)
            {
                _logger.LogWarning(
                    "MercadoLivre OAuth callback failed without exception. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                    payload.TenantId,
                    payload.ClientId,
                    HttpContext.TraceIdentifier);
                return Redirect(BuildClientRedirectTarget(AppendQuery(target, "ml", "oauth_error")));
            }

            return Redirect(BuildClientRedirectTarget(AppendQuery(target, "ml", "connected")));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "MercadoLivre OAuth callback failed with exception. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                payload.TenantId,
                payload.ClientId,
                HttpContext.TraceIdentifier);
            return Redirect(BuildClientRedirectTarget(AppendQuery(target, "ml", "oauth_error")));
        }
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect([FromBody] MercadoLivreDisconnectRequest? request, CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        var result = await _integrationService.DisconnectAsync(tenantId!, clientId, request?.SellerId, cancellationToken);
        if (!result.Succeeded)
        {
            return MapValidationError(result.Errors);
        }

        return NoContent();
    }

    [HttpPost("sync-now")]
    public async Task<IActionResult> SyncNow([FromBody] MercadoLivreSyncNowRequest? request, CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        var result = await _syncService.SyncNowAsync(tenantId!, clientId, request?.SellerId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapValidationError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpGet("mappings")]
    public async Task<IActionResult> ListMappings([FromQuery] string? sellerId, CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        var result = await _mappingService.ListAsync(tenantId!, clientId, sellerId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapValidationError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpPost("mappings")]
    public async Task<IActionResult> CreateMapping([FromBody] MercadoLivreCreateMappingRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        var result = await _mappingService.CreateAsync(tenantId!, clientId, request, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapValidationError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpDelete("mappings/{id:guid}")]
    public async Task<IActionResult> DeleteMapping(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        var result = await _mappingService.DeleteAsync(tenantId!, clientId, id, cancellationToken);
        if (!result.Succeeded)
        {
            return MapValidationError(result.Errors);
        }

        return NoContent();
    }

    [HttpPost("publish/validate")]
    public async Task<IActionResult> ValidatePublish(
        [FromBody] MercadoLivrePublishValidateRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        var result = await _publishValidationService.ValidateAsync(
            tenantId!,
            clientId,
            request ?? new MercadoLivrePublishValidateRequest(),
            cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapValidationError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpPost("publish")]
    public async Task<IActionResult> Publish(
        [FromBody] MercadoLivrePublishRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        var result = await _publishService.PublishAsync(
            tenantId!,
            clientId,
            request ?? new MercadoLivrePublishRequest(),
            cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapValidationError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpGet("listings")]
    public async Task<IActionResult> ListListings([FromQuery] string? sellerId, CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        var result = await _publishService.ListListingsAsync(tenantId!, clientId, sellerId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapValidationError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpPost("reconcile")]
    public async Task<IActionResult> Reconcile([FromBody] MercadoLivreSyncNowRequest? request, CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        var result = await _publishService.ReconcileAsync(tenantId!, clientId, request?.SellerId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapValidationError(result.Errors);
        }

        return Ok(result.Data);
    }

    private static string AppendQuery(string path, string key, string value)
    {
        if (path.Contains('?', StringComparison.Ordinal))
        {
            return $"{path}&{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
        }

        return $"{path}?{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
    }

    private string BuildClientRedirectTarget(string targetPathOrUrl)
    {
        if (Uri.TryCreate(targetPathOrUrl, UriKind.Absolute, out var absoluteTarget))
        {
            return absoluteTarget.ToString();
        }

        var normalizedPath = targetPathOrUrl.StartsWith("/", StringComparison.Ordinal)
            ? targetPathOrUrl
            : $"/{targetPathOrUrl}";

        if (!string.IsNullOrWhiteSpace(_mercadoLivreOptions.ClientPortalBaseUrl) &&
            Uri.TryCreate(_mercadoLivreOptions.ClientPortalBaseUrl, UriKind.Absolute, out var clientBase))
        {
            return new Uri(clientBase, normalizedPath).ToString();
        }

        return normalizedPath;
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

        if (errors.Any(item => string.Equals(item.Message, "ML_UNMAPPED_ITEM", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("ML_UNMAPPED_ITEM", "Order has unmapped items", errors));
        }

        if (errors.Any(item => string.Equals(item.Field, "sabrVariantSku", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("SKU_NOT_FOUND", "Variant SKU not found", errors));
        }

        if (errors.Any(item => string.Equals(item.Field, "sellerId", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("SELLER_NOT_FOUND", "Mercado Livre seller connection not found", errors));
        }

        if (errors.Any(item => string.Equals(item.Message, "ML_PUBLISH_DISABLED", StringComparison.OrdinalIgnoreCase)))
        {
            return StatusCode(StatusCodes.Status403Forbidden, CreateApiError("ML_PUBLISH_DISABLED", "Mercado Livre publish is disabled", errors));
        }

        if (errors.Any(item => string.Equals(item.Message, "ML_RECONCILE_DISABLED", StringComparison.OrdinalIgnoreCase)))
        {
            return StatusCode(StatusCodes.Status403Forbidden, CreateApiError("ML_RECONCILE_DISABLED", "Mercado Livre reconcile is disabled", errors));
        }

        if (errors.Any(item => string.Equals(item.Field, "scope", StringComparison.OrdinalIgnoreCase)))
        {
            return UnprocessableEntity(CreateApiError("PUBLISH_SCOPE_EMPTY", "No variants found for publish scope", errors));
        }

        if (errors.Any(item => string.Equals(item.Field, "id", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(item.Field, "orderId", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("NOT_FOUND", "Resource not found", errors));
        }

        return BadRequest(CreateApiError("VALIDATION_ERROR", "Invalid request", errors));
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
