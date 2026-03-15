using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sabr.Api.Security;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Services;
using Sabr.Application.Validation;
using Sabr.Domain.Enums;

namespace Sabr.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/client/integrations/tinyerp")]
public sealed class ClientTinyIntegrationController : ControllerBase
{
    private readonly ILogger<ClientTinyIntegrationController> _logger;
    private readonly ITenantProvider _tenantProvider;
    private readonly TinyOAuthService _oauthService;
    private readonly TinyIntegrationService _integrationService;

    public ClientTinyIntegrationController(
        ILogger<ClientTinyIntegrationController> logger,
        ITenantProvider tenantProvider,
        TinyOAuthService oauthService,
        TinyIntegrationService integrationService)
    {
        _logger = logger;
        _tenantProvider = tenantProvider;
        _oauthService = oauthService;
        _integrationService = integrationService;
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
            var result = await _integrationService.GetClientStatusAsync(tenantId!, clientId, cancellationToken);
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
                "Failed to load Tiny ERP status. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId,
                clientId,
                HttpContext.TraceIdentifier);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                CreateApiError("TINY_STATUS_INTERNAL_ERROR", "Falha interna ao carregar status da integracao Tiny ERP"));
        }
    }

    [HttpPost("connect-url")]
    public IActionResult CreateConnectUrl(CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        if (!_oauthService.IsAppConfigured(out var configurationMessage))
        {
            return BadRequest(CreateApiError("TINY_APP_NOT_CONFIGURED", configurationMessage));
        }

        var state = $"{tenantId}:{clientId}:{Guid.NewGuid():N}";
        var connectUrl = _oauthService.BuildConnectUrl(state);
        return Ok(new { url = connectUrl });
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
            return Redirect("/client/integrations/tinyerp?tiny=missing_code_or_state");
        }

        // State format: "{tenantId}:{clientId}:{nonce}"
        var parts = state.Split(':', 3);
        if (parts.Length < 2 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            !Guid.TryParse(parts[1], out var clientId))
        {
            return Redirect("/client/integrations/tinyerp?tiny=invalid_state");
        }

        var tenantId = parts[0];
        try
        {
            var result = await _oauthService.HandleCallbackAsync(tenantId, clientId, code, cancellationToken);
            if (!result.Succeeded || result.Data == null)
            {
                _logger.LogWarning(
                    "Tiny ERP OAuth callback failed. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                    tenantId,
                    clientId,
                    HttpContext.TraceIdentifier);
                return Redirect("/client/integrations/tinyerp?tiny=oauth_error");
            }

            return Redirect("/client/integrations/tinyerp?tiny=connected");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Tiny ERP OAuth callback exception. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId,
                clientId,
                HttpContext.TraceIdentifier);
            return Redirect("/client/integrations/tinyerp?tiny=oauth_error");
        }
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        var result = await _integrationService.DisconnectAsync(tenantId!, clientId, cancellationToken);
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

        var result = await _integrationService.SyncOrdersAsync(tenantId!, clientId, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            return MapValidationError(result.Errors);
        }

        return Ok(result.Data);
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

        if (errors.Any(e => string.Equals(e.Field, "connection", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound(CreateApiError("TINY_NOT_CONNECTED", "Tiny ERP integration not found", errors));
        }

        if (errors.Any(e => string.Equals(e.Message, "TINY_AUTH_INVALID", StringComparison.OrdinalIgnoreCase)))
        {
            return StatusCode(StatusCodes.Status401Unauthorized,
                CreateApiError("TINY_AUTH_INVALID", "Tiny ERP token is invalid or expired. Please reconnect.", errors));
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
