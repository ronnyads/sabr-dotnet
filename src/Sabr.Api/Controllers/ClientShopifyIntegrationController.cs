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
[Route("api/v1/client/integrations/shopify")]
public sealed class ClientShopifyIntegrationController : ControllerBase
{
    private readonly ILogger<ClientShopifyIntegrationController> _logger;
    private readonly ITenantProvider _tenantProvider;
    private readonly ShopifyOAuthStateService _oauthStateService;
    private readonly ShopifyOAuthService _oauthService;
    private readonly ShopifyOptions _shopifyOptions;

    public ClientShopifyIntegrationController(
        ILogger<ClientShopifyIntegrationController> logger,
        ITenantProvider tenantProvider,
        ShopifyOAuthStateService oauthStateService,
        ShopifyOAuthService oauthService,
        IOptions<ShopifyOptions> shopifyOptions)
    {
        _logger = logger;
        _tenantProvider = tenantProvider;
        _oauthStateService = oauthStateService;
        _oauthService = oauthService;
        _shopifyOptions = shopifyOptions.Value;
    }

    // ── GET /status ───────────────────────────────────────────────────────────

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
                "Failed to load Shopify status. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId, clientId, HttpContext.TraceIdentifier);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                CreateApiError("SHOPIFY_STATUS_INTERNAL_ERROR", "Falha interna ao carregar status da integração Shopify"));
        }
    }

    // ── POST /connect-url ─────────────────────────────────────────────────────

    [HttpPost("connect-url")]
    public IActionResult CreateConnectUrl([FromBody] ShopifyConnectUrlRequest? request)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        if (string.IsNullOrWhiteSpace(request?.Shop))
        {
            return BadRequest(CreateApiError("SHOPIFY_SHOP_REQUIRED", "O domínio da loja Shopify é obrigatório (ex: minha-loja.myshopify.com)"));
        }

        if (!_oauthService.IsAppConfigured(out var configurationMessage))
        {
            return BadRequest(CreateApiError("SHOPIFY_APP_NOT_CONFIGURED", configurationMessage));
        }

        var state = _oauthStateService.CreateState(tenantId!, clientId, request.Shop, request.ReturnUrl);
        var connectUrl = _oauthService.BuildConnectUrl(state, request.Shop);
        return Ok(new ShopifyConnectUrlResult { Url = connectUrl });
    }

    // ── GET /callback ─────────────────────────────────────────────────────────

    [AllowAnonymous]
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? shop,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return Redirect(BuildClientRedirectTarget("/client/integrations/shopify?shopify=missing_code_or_state"));
        }

        if (!_oauthStateService.TryReadState(state, out var payload))
        {
            return Redirect(BuildClientRedirectTarget("/client/integrations/shopify?shopify=invalid_state"));
        }

        // Use shop from state (more trustworthy than query param)
        var shopDomain = payload.Shop;
        var target = payload.ReturnUrl;

        try
        {
            var result = await _oauthService.HandleCallbackAsync(
                payload.TenantId, payload.ClientId, shopDomain, code, cancellationToken);

            if (!result.Succeeded || result.Data == null)
            {
                _logger.LogWarning(
                    "Shopify OAuth callback failed without exception. tenantId={TenantId} clientId={ClientId} shop={Shop} traceId={TraceId}",
                    payload.TenantId, payload.ClientId, shopDomain, HttpContext.TraceIdentifier);
                return Redirect(BuildClientRedirectTarget(AppendQuery(target, "shopify", "oauth_error")));
            }

            return Redirect(BuildClientRedirectTarget(AppendQuery(target, "shopify", "connected")));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Shopify OAuth callback failed with exception. tenantId={TenantId} clientId={ClientId} shop={Shop} traceId={TraceId}",
                payload.TenantId, payload.ClientId, shopDomain, HttpContext.TraceIdentifier);
            return Redirect(BuildClientRedirectTarget(AppendQuery(target, "shopify", "oauth_error")));
        }
    }

    // ── POST /disconnect ──────────────────────────────────────────────────────

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

    // ── POST /sync-now ────────────────────────────────────────────────────────

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
                "Shopify sync-now failed. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId, clientId, HttpContext.TraceIdentifier);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                CreateApiError("SHOPIFY_SYNC_INTERNAL_ERROR", "Falha interna ao sincronizar pedidos do Shopify"));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string BuildClientRedirectTarget(string targetPathOrUrl)
    {
        if (Uri.TryCreate(targetPathOrUrl, UriKind.Absolute, out var absoluteTarget))
        {
            return absoluteTarget.ToString();
        }

        var normalizedPath = targetPathOrUrl.StartsWith("/", StringComparison.Ordinal)
            ? targetPathOrUrl
            : $"/{targetPathOrUrl}";

        if (!string.IsNullOrWhiteSpace(_shopifyOptions.ClientPortalBaseUrl) &&
            Uri.TryCreate(_shopifyOptions.ClientPortalBaseUrl, UriKind.Absolute, out var clientBase))
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
            return NotFound(CreateApiError("SHOPIFY_NOT_CONNECTED", "Shopify connection not found", errors));
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

// ── Request / Response types ──────────────────────────────────────────────────

public sealed class ShopifyConnectUrlRequest
{
    public string Shop { get; set; } = string.Empty;       // e.g. "minha-loja.myshopify.com"
    public string? ReturnUrl { get; set; }
}

public sealed class ShopifyConnectUrlResult
{
    public string Url { get; set; } = string.Empty;
}
