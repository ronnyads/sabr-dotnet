using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Domain.Enums;

namespace Sabr.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/client/integrations")]
public sealed class ClientIntegrationsHubController : ControllerBase
{
    private readonly ILogger<ClientIntegrationsHubController> _logger;
    private readonly ITenantProvider _tenantProvider;
    private readonly IAppDbContext _db;

    public ClientIntegrationsHubController(
        ILogger<ClientIntegrationsHubController> logger,
        ITenantProvider tenantProvider,
        IAppDbContext db)
    {
        _logger = logger;
        _tenantProvider = tenantProvider;
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetIntegrations(CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error))
        {
            return error!;
        }

        try
        {
            var connections = await _db.TenantMarketplaceConnections
                .Where(c => c.TenantId == tenantId && c.ClientId == clientId)
                .Select(c => new
                {
                    c.Provider,
                    c.Nickname,
                    c.CreatedAt,
                    c.LastSyncAt
                })
                .ToListAsync(cancellationToken);

            var mlConn = connections.FirstOrDefault(c => c.Provider == MarketplaceProvider.MercadoLivre);
            var tinyConn = connections.FirstOrDefault(c => c.Provider == MarketplaceProvider.TinyErp);
            var shopifyConn = connections.FirstOrDefault(c => c.Provider == MarketplaceProvider.Shopify);
            var tikTokConn = connections.FirstOrDefault(c => c.Provider == MarketplaceProvider.TikTokShop);

            var result = new List<ClientIntegrationCardResult>
            {
                new()
                {
                    Provider = (int)MarketplaceProvider.MercadoLivre,
                    Name = "Mercado Livre",
                    Description = "Sincronize pedidos e produtos com o Mercado Livre.",
                    IsConnected = mlConn != null,
                    ConnectedAt = mlConn?.CreatedAt.UtcDateTime,
                    LastSyncAt = mlConn?.LastSyncAt?.UtcDateTime,
                    Details = mlConn?.Nickname
                },
                new()
                {
                    Provider = (int)MarketplaceProvider.TinyErp,
                    Name = "Tiny ERP",
                    Description = "Integre pedidos e emissao de notas fiscais com o Tiny ERP.",
                    IsConnected = tinyConn != null,
                    ConnectedAt = tinyConn?.CreatedAt.UtcDateTime,
                    LastSyncAt = tinyConn?.LastSyncAt?.UtcDateTime,
                    Details = tinyConn?.Nickname
                },
                new()
                {
                    Provider = (int)MarketplaceProvider.Shopify,
                    Name = "Shopify",
                    Description = "Sincronize pedidos e inventario com a sua loja Shopify.",
                    IsConnected = shopifyConn != null,
                    ConnectedAt = shopifyConn?.CreatedAt.UtcDateTime,
                    LastSyncAt = shopifyConn?.LastSyncAt?.UtcDateTime,
                    Details = shopifyConn?.Nickname
                },
                new()
                {
                    Provider = (int)MarketplaceProvider.TikTokShop,
                    Name = "TikTok Shop",
                    Description = "Conecte sua operacao ao TikTok Shop para preparar sincronizacao de pedidos e catalogo.",
                    IsConnected = tikTokConn != null,
                    ConnectedAt = tikTokConn?.CreatedAt.UtcDateTime,
                    LastSyncAt = tikTokConn?.LastSyncAt?.UtcDateTime,
                    Details = tikTokConn?.Nickname
                }
            };

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Client: failed to load integrations hub. tenantId={TenantId} clientId={ClientId} traceId={TraceId}",
                tenantId,
                clientId,
                HttpContext.TraceIdentifier);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                CreateApiError("INTEGRATIONS_HUB_INTERNAL_ERROR", "Falha interna ao carregar hub de integracoes"));
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
