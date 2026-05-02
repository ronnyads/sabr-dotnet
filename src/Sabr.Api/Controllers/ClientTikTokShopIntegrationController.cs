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
[Route("api/v1/client/integrations/tiktokshop")]
public sealed class ClientTikTokShopIntegrationController : ControllerBase
{
    private readonly ILogger<ClientTikTokShopIntegrationController> _logger;
    private readonly ITenantProvider _tenantProvider;
    private readonly TikTokShopOAuthStateService _oauthStateService;
    private readonly TikTokShopOAuthService _oauthService;
    private readonly TikTokShopSyncService _syncService;
    private readonly TikTokShopMappingService _mappingService;
    private readonly TikTokShopPublishService _publishService;
    private readonly TikTokShopOptions _options;

    public ClientTikTokShopIntegrationController(
        ILogger<ClientTikTokShopIntegrationController> logger,
        ITenantProvider tenantProvider,
        TikTokShopOAuthStateService oauthStateService,
        TikTokShopOAuthService oauthService,
        TikTokShopSyncService syncService,
        TikTokShopMappingService mappingService,
        TikTokShopPublishService publishService,
        IOptions<TikTokShopOptions> options)
    {
        _logger = logger;
        _tenantProvider = tenantProvider;
        _oauthStateService = oauthStateService;
        _oauthService = oauthService;
        _syncService = syncService;
        _mappingService = mappingService;
        _publishService = publishService;
        _options = options.Value;
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
            var result = await _oauthService.GetClientStatusAsync(tenantId!, clientId, cancellationToken);
            return Ok(result.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to load TikTok Shop status. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId, clientId, HttpContext.TraceIdentifier);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                CreateApiError("TIKTOK_SHOP_STATUS_INTERNAL_ERROR", "Falha interna ao carregar status da integração TikTok Shop"));
        }
    }

    [HttpPost("connect-url")]
    public IActionResult CreateConnectUrl([FromBody] TikTokShopConnectUrlRequest? request)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        if (!_oauthService.IsAppConfigured(out var configurationMessage))
        {
            return BadRequest(CreateApiError("TIKTOK_SHOP_APP_NOT_CONFIGURED", configurationMessage));
        }

        var state = _oauthStateService.CreateState(tenantId!, clientId, request?.ReturnUrl);
        var connectUrl = _oauthService.BuildConnectUrl(state);
        return Ok(new TikTokShopConnectUrlResult { Url = connectUrl });
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
            return Redirect(BuildClientRedirectTarget("/client/integrations/tiktokshop?tiktok=missing_code_or_state"));
        }

        if (!_oauthStateService.TryReadState(state, out var payload))
        {
            return Redirect(BuildClientRedirectTarget("/client/integrations/tiktokshop?tiktok=invalid_state"));
        }

        try
        {
            var result = await _oauthService.HandleCallbackAsync(
                payload.TenantId, payload.ClientId, code, cancellationToken);

            if (!result.Succeeded || result.Data == null)
            {
                _logger.LogWarning(
                    "TikTok Shop OAuth callback failed without exception. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                    payload.TenantId, payload.ClientId, HttpContext.TraceIdentifier);
                return Redirect(BuildClientRedirectTarget(AppendQuery(payload.ReturnUrl, "tiktok", "oauth_error")));
            }

            return Redirect(BuildClientRedirectTarget(AppendQuery(payload.ReturnUrl, "tiktok", "connected")));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "TikTok Shop OAuth callback failed with exception. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                payload.TenantId, payload.ClientId, HttpContext.TraceIdentifier);
            return Redirect(BuildClientRedirectTarget(AppendQuery(payload.ReturnUrl, "tiktok", "oauth_error")));
        }
    }

    [HttpPost("reset")]
    public async Task<IActionResult> Reset(CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        var result = await _oauthService.ResetAsync(tenantId!, clientId, cancellationToken);
        if (!result.Succeeded)
        {
            return MapValidationError(result.Errors);
        }

        return NoContent();
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        var result = await _oauthService.DisconnectAsync(tenantId!, clientId, cancellationToken);
        if (!result.Succeeded)
        {
            return MapValidationError(result.Errors);
        }

        return NoContent();
    }

    [HttpPost("sync-now")]
    public async Task<IActionResult> SyncNow(CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        try
        {
            var result = await _syncService.SyncOrdersAsync(tenantId!, clientId, cancellationToken);
            if (!result.Succeeded)
            {
                return MapValidationError(result.Errors);
            }

            return Ok(result.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "TikTok Shop sync-now failed. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId, clientId, HttpContext.TraceIdentifier);
            return StatusCode(StatusCodes.Status500InternalServerError,
                CreateApiError("TIKTOK_SHOP_SYNC_ERROR", "Falha ao sincronizar pedidos TikTok Shop"));
        }
    }

    [HttpGet("mappings")]
    public async Task<IActionResult> ListMappings(CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        var result = await _mappingService.ListMappingsAsync(tenantId!, clientId, cancellationToken);
        return Ok(result.Data);
    }

    [HttpPost("mappings")]
    public async Task<IActionResult> CreateMapping(
        [FromBody] TikTokShopCreateMappingRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        var result = await _mappingService.CreateMappingAsync(tenantId!, clientId, request, cancellationToken);
        if (!result.Succeeded)
        {
            return MapValidationError(result.Errors);
        }

        return StatusCode(StatusCodes.Status201Created, result.Data);
    }

    [HttpDelete("mappings/{id:guid}")]
    public async Task<IActionResult> DeleteMapping(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        var result = await _mappingService.DeleteMappingAsync(tenantId!, clientId, id, cancellationToken);
        if (!result.Succeeded)
        {
            return MapValidationError(result.Errors);
        }

        return NoContent();
    }

    [HttpPost("publish/validate")]
    public async Task<IActionResult> ValidatePublish(
        [FromBody] TikTokShopPublishRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        var result = await _publishService.ValidatePublishAsync(tenantId!, clientId, request, cancellationToken);
        if (!result.Succeeded)
        {
            return MapValidationError(result.Errors);
        }

        return Ok(result.Data);
    }

    [HttpPost("publish")]
    public async Task<IActionResult> Publish(
        [FromBody] TikTokShopPublishRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        try
        {
            var result = await _publishService.PublishAsync(tenantId!, clientId, request, cancellationToken);
            if (!result.Succeeded)
            {
                return MapValidationError(result.Errors);
            }

            return Ok(result.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "TikTok Shop publish failed. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId, clientId, HttpContext.TraceIdentifier);
            return StatusCode(StatusCodes.Status500InternalServerError,
                CreateApiError("TIKTOK_SHOP_PUBLISH_ERROR", "Falha ao publicar produtos no TikTok Shop"));
        }
    }

    [HttpGet("listings")]
    public async Task<IActionResult> ListListings(CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        var result = await _publishService.ListListingsAsync(tenantId!, clientId, cancellationToken);
        return Ok(result.Data);
    }

    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories(CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        try
        {
            var result = await _publishService.GetCategoriesAsync(tenantId!, clientId, cancellationToken);
            if (!result.Succeeded)
            {
                return MapValidationError(result.Errors);
            }

            return Ok(result.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "TikTok Shop get categories failed. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId, clientId, HttpContext.TraceIdentifier);
            return StatusCode(StatusCodes.Status500InternalServerError,
                CreateApiError("TIKTOK_SHOP_CATEGORIES_ERROR", "Falha ao buscar categorias TikTok Shop"));
        }
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

        if (!string.IsNullOrWhiteSpace(_options.ClientPortalBaseUrl) &&
            Uri.TryCreate(_options.ClientPortalBaseUrl, UriKind.Absolute, out var clientBase))
        {
            return new Uri(clientBase, normalizedPath).ToString();
        }

        return normalizedPath;
    }

    private static string AppendQuery(string url, string key, string value)
    {
        var separator = url.Contains('?') ? "&" : "?";
        return $"{url}{separator}{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
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

        if (errors.Any(item => string.Equals(item.Field, "connection", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("TIKTOK_SHOP_NOT_CONNECTED", "TikTok Shop connection not found", errors));
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

public sealed class TikTokShopConnectUrlRequest
{
    public string? ReturnUrl { get; set; }
}

public sealed class TikTokShopConnectUrlResult
{
    public string Url { get; set; } = string.Empty;
}
