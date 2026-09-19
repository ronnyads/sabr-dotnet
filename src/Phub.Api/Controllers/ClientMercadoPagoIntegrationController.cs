using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Phub.Api.Security;
using Phub.Application.Abstractions;
using Phub.Application.Options;
using Phub.Domain.Enums;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/client/integrations/mercadopago")]
public sealed class ClientMercadoPagoIntegrationController : ControllerBase
{
    private readonly ITenantProvider _tenant;
    private readonly IAppDbContext _db;
    private readonly MercadoLivreOAuthStateService _state;
    private readonly MercadoPagoOAuthService _oauth;
    private readonly MercadoPagoOptions _options;
    private readonly ILogger<ClientMercadoPagoIntegrationController> _logger;

    public ClientMercadoPagoIntegrationController(
        ITenantProvider tenant,
        IAppDbContext db,
        MercadoLivreOAuthStateService state,
        MercadoPagoOAuthService oauth,
        IOptions<MercadoPagoOptions> options,
        ILogger<ClientMercadoPagoIntegrationController> logger)
    {
        _tenant = tenant;
        _db = db;
        _state = state;
        _oauth = oauth;
        _options = options.Value;
        _logger = logger;
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        if (!TryGetClient(out var tenantId, out var clientId, out var error)) return error!;
        var grants = await _db.MarketplaceOAuthGrants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientId == clientId &&
                        x.Provider == MarketplaceProvider.MercadoLivre && x.AppFamily == "MERCADO_PAGO")
            .Select(x => new
            {
                x.SellerId,
                Connected = !x.RequiresReauthorization && x.TokenExpiresAt > DateTimeOffset.UtcNow,
                x.TokenExpiresAt,
                x.LastCapabilityVerifiedAt,
                x.RequiresReauthorization,
                x.CapabilitiesJson
            })
            .ToListAsync(cancellationToken);
        return Ok(new
        {
            configured = _oauth.IsConfigured(out _),
            connected = grants.Any(x => x.Connected),
            billingVerified = grants.Any(x => x.Connected && x.LastCapabilityVerifiedAt.HasValue &&
                x.CapabilitiesJson.Contains("\"billingMercadoPago\":true")),
            grants = grants.Select(x => new { x.SellerId, x.Connected, x.TokenExpiresAt,
                x.LastCapabilityVerifiedAt, x.RequiresReauthorization })
        });
    }

    [HttpPost("connect-url")]
    public IActionResult ConnectUrl([FromBody] MercadoPagoConnectUrlRequest? request)
    {
        if (!TryGetClient(out var tenantId, out var clientId, out var error)) return error!;
        if (!_oauth.IsConfigured(out var message)) return BadRequest(new { code = "MP_APP_NOT_CONFIGURED", message });
        var state = _state.CreateState(tenantId!, clientId, request?.ReturnUrl ?? "/client/integrations/mercadolivre");
        return Ok(new { url = _oauth.BuildConnectUrl(state) });
    }

    [AllowAnonymous]
    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            return Redirect(BuildClientRedirect("/client/integrations/mercadolivre?mp=missing_code_or_state"));
        if (!_state.TryReadState(state, out var payload))
            return Redirect(BuildClientRedirect("/client/integrations/mercadolivre?mp=invalid_state"));

        try
        {
            await _oauth.HandleCallbackAsync(payload.TenantId, payload.ClientId, code, cancellationToken);
            return Redirect(BuildClientRedirect(AppendQuery(payload.ReturnUrl, "mp", "connected")));
        }
        catch (InvalidOperationException ex) when (ex.Message == "MP_SELLER_MISMATCH")
        {
            return Redirect(BuildClientRedirect(AppendQuery(payload.ReturnUrl, "mp", "seller_mismatch")));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mercado Pago OAuth callback failed. tenantId={TenantId} clientId={ClientId}",
                payload.TenantId, payload.ClientId);
            return Redirect(BuildClientRedirect(AppendQuery(payload.ReturnUrl, "mp", "oauth_error")));
        }
    }

    private bool TryGetClient(out string? tenantId, out Guid clientId, out IActionResult? error)
    {
        tenantId = _tenant.TenantId;
        clientId = Guid.Empty;
        error = null;
        if (!string.Equals(User.FindFirst("accountType")?.Value, AccountTypes.Client, StringComparison.OrdinalIgnoreCase))
        {
            error = Forbid();
            return false;
        }
        if (string.IsNullOrWhiteSpace(tenantId) || !Guid.TryParse(User.FindFirst("clientId")?.Value, out clientId))
        {
            error = Unauthorized(new { code = "INVALID_CLIENT_CONTEXT" });
            return false;
        }
        return true;
    }

    private string BuildClientRedirect(string path)
    {
        if (!string.IsNullOrWhiteSpace(_options.ClientPortalBaseUrl) &&
            Uri.TryCreate(_options.ClientPortalBaseUrl, UriKind.Absolute, out var baseUri))
            return new Uri(baseUri, path.StartsWith('/') ? path : $"/{path}").ToString();
        return path;
    }

    private static string AppendQuery(string path, string key, string value)
        => path.Contains('?') ? $"{path}&{key}={value}" : $"{path}?{key}={value}";
}

public sealed class MercadoPagoConnectUrlRequest
{
    public string? ReturnUrl { get; set; }
}
