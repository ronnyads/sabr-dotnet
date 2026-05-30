using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Phub.Api.Security;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Options;
using Phub.Application.Services;
using Phub.Application.Validation;
using Phub.Domain.Enums;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/client/integrations/shopee")]
public sealed class ClientShopeeIntegrationController : ControllerBase
{
    private readonly ILogger<ClientShopeeIntegrationController> _logger;
    private readonly ITenantProvider _tenantProvider;
    private readonly ShopeeOAuthStateService _oauthStateService;
    private readonly ShopeeOAuthService _oauthService;
    private readonly ShopeeOptions _options;

    public ClientShopeeIntegrationController(
        ILogger<ClientShopeeIntegrationController> logger,
        ITenantProvider tenantProvider,
        ShopeeOAuthStateService oauthStateService,
        ShopeeOAuthService oauthService,
        IOptions<ShopeeOptions> options)
    {
        _logger = logger;
        _tenantProvider = tenantProvider;
        _oauthStateService = oauthStateService;
        _oauthService = oauthService;
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
                "Failed to load Shopee status. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId,
                clientId,
                HttpContext.TraceIdentifier);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                CreateApiError("SHOPEE_STATUS_INTERNAL_ERROR", "Falha interna ao carregar status da integracao Shopee"));
        }
    }

    [HttpPost("connect-url")]
    public IActionResult CreateConnectUrl([FromBody] ShopeeConnectUrlRequest? request)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        if (!_oauthService.IsAppConfigured(out var configurationMessage))
        {
            return BadRequest(CreateApiError("SHOPEE_APP_NOT_CONFIGURED", configurationMessage));
        }

        var state = _oauthStateService.CreateState(tenantId!, clientId, request?.ReturnUrl);
        var connectUrl = _oauthService.BuildConnectUrl(state);
        return Ok(new ShopeeConnectUrlResult { Url = connectUrl });
    }

    [AllowAnonymous]
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery(Name = "shop_id")] long? shopId,
        [FromQuery(Name = "main_account_id")] long? mainAccountId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return TopLevelRedirect(BuildClientRedirectTarget("/client/integrations/shopee?shopee=missing_code_or_state"));
        }

        if (!_oauthStateService.TryReadState(state, out var payload))
        {
            return TopLevelRedirect(BuildClientRedirectTarget("/client/integrations/shopee?shopee=invalid_state"));
        }

        try
        {
            var result = await _oauthService.HandleCallbackAsync(
                payload.TenantId,
                payload.ClientId,
                code,
                shopId,
                mainAccountId,
                cancellationToken);

            if (!result.Succeeded || result.Data == null)
            {
                _logger.LogWarning(
                    "Shopee OAuth callback failed without exception. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                    payload.TenantId,
                    payload.ClientId,
                    HttpContext.TraceIdentifier);
                return TopLevelRedirect(BuildClientRedirectTarget(AppendQuery(payload.ReturnUrl, "shopee", "oauth_error")));
            }

            try
            {
                await _oauthService.SyncNowAsync(payload.TenantId, payload.ClientId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Shopee post-connect sync failed. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                    payload.TenantId,
                    payload.ClientId,
                    HttpContext.TraceIdentifier);
            }

            return TopLevelRedirect(BuildClientRedirectTarget(AppendQuery(payload.ReturnUrl, "shopee", "connected")));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Shopee OAuth callback failed with exception. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                payload.TenantId,
                payload.ClientId,
                HttpContext.TraceIdentifier);
            return TopLevelRedirect(BuildClientRedirectTarget(AppendQuery(payload.ReturnUrl, "shopee", "oauth_error")));
        }
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
            var result = await _oauthService.SyncNowAsync(tenantId!, clientId, cancellationToken);
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
                "Shopee sync-now failed. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId,
                clientId,
                HttpContext.TraceIdentifier);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                CreateApiError("SHOPEE_SYNC_INTERNAL_ERROR", "Falha interna ao sincronizar pedidos da Shopee"));
        }
    }

    private ContentResult TopLevelRedirect(string url)
    {
        var htmlSafe = System.Text.Encodings.Web.HtmlEncoder.Default.Encode(url);
        var jsSafe = url.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "");
        return Content($$"""
            <!DOCTYPE html>
            <html><head><meta http-equiv="refresh" content="0;url={{htmlSafe}}"></head>
            <body><script>try{window.top.location.href="{{jsSafe}}"}catch(_){window.location.href="{{jsSafe}}"}</script></body>
            </html>
            """, "text/html");
    }

    private string BuildClientRedirectTarget(string targetPathOrUrl)
    {
        if (Uri.TryCreate(targetPathOrUrl, UriKind.Absolute, out var absoluteTarget) &&
            (absoluteTarget.Scheme == Uri.UriSchemeHttp || absoluteTarget.Scheme == Uri.UriSchemeHttps))
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

        if (errors.Any(item => string.Equals(item.Field, "shop", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("SHOPEE_NOT_CONNECTED", "Shopee connection not found", errors));
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

public sealed class ShopeeConnectUrlRequest
{
    public string? ReturnUrl { get; set; }
}

public sealed class ShopeeConnectUrlResult
{
    public string Url { get; set; } = string.Empty;
}
