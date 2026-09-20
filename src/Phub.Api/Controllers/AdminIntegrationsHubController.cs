using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Enums;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/integrations")]
public sealed class AdminIntegrationsHubController : ControllerBase
{
    private readonly ILogger<AdminIntegrationsHubController> _logger;
    private readonly IAppDbContext _db;

    public AdminIntegrationsHubController(
        ILogger<AdminIntegrationsHubController> logger,
        IAppDbContext db)
    {
        _logger = logger;
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetIntegrations(CancellationToken cancellationToken)
    {
        try
        {
            var connections = await _db.TenantMarketplaceConnections
                .GroupBy(c => c.Provider)
                .Select(g => new { Provider = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);

            var counts = connections.ToDictionary(item => item.Provider, item => item.Count);
            var mercadoPagoConnectedCount = await _db.MarketplaceOAuthGrants
                .Where(x => x.AppFamily == "MERCADO_PAGO" &&
                            !x.RequiresReauthorization &&
                            (x.TokenExpiresAt > DateTimeOffset.UtcNow || x.RefreshTokenProtected != ""))
                .Select(x => x.ClientId)
                .Distinct()
                .CountAsync(cancellationToken);

            var result = new List<IntegrationCardResult>
            {
                BuildIntegrationCard(
                    MarketplaceProvider.MercadoLivre,
                    "Mercado Livre",
                    "Sincronize pedidos e produtos com o Mercado Livre.",
                    counts),
                new()
                {
                    Provider = 100,
                    Slug = "mercadopago",
                    Category = "Financeiro",
                    Name = "Mercado Pago",
                    Description = "Autorize a conciliação oficial de pagamentos, tarifas, estornos e ajustes.",
                    ConnectedCount = mercadoPagoConnectedCount
                },
                BuildIntegrationCard(
                    MarketplaceProvider.TinyErp,
                    "Tiny ERP",
                    "Integre pedidos e emissao de notas fiscais com o Tiny ERP.",
                    counts),
                BuildIntegrationCard(
                    MarketplaceProvider.Shopify,
                    "Shopify",
                    "Sincronize pedidos e inventario com a loja Shopify.",
                    counts),
                BuildIntegrationCard(
                    MarketplaceProvider.TikTokShop,
                    "TikTok Shop",
                    "Conecte contas TikTok Shop e prepare a operacao para sincronizacao futura.",
                    counts),
                BuildIntegrationCard(
                    MarketplaceProvider.Shopee,
                    "Shopee",
                    "Conecte contas Shopee com OAuth oficial e sincronizacao de pedidos.",
                    counts)
            };

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin: failed to load integrations hub. traceId={TraceId}", HttpContext.TraceIdentifier);
            return StatusCode(500, new { code = "INTEGRATIONS_HUB_ERROR", message = "Falha ao carregar integracoes." });
        }
    }

    [HttpGet("mercadolivre/clients")]
    public async Task<IActionResult> GetMercadoLivreClients(
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        var result = await GetClientsByProvider(MarketplaceProvider.MercadoLivre, skip, limit, search, cancellationToken);
        return Ok(result);
    }

    [HttpGet("tinyerp/clients")]
    public async Task<IActionResult> GetTinyErpClients(
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        var result = await GetClientsByProvider(MarketplaceProvider.TinyErp, skip, limit, search, cancellationToken);
        return Ok(result);
    }

    [HttpGet("shopify/clients")]
    public async Task<IActionResult> GetShopifyClients(
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        var result = await GetClientsByProvider(MarketplaceProvider.Shopify, skip, limit, search, cancellationToken);
        return Ok(result);
    }

    [HttpGet("tiktokshop/clients")]
    public async Task<IActionResult> GetTikTokShopClients(
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        var result = await GetClientsByProvider(MarketplaceProvider.TikTokShop, skip, limit, search, cancellationToken);
        return Ok(result);
    }

    [HttpGet("mercadopago/clients")]
    public async Task<IActionResult> GetMercadoPagoClients(
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        var clientsQuery = _db.Clients.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalizedSearch = search.Trim().ToLower();
            clientsQuery = clientsQuery.Where(c =>
                c.AccountName.ToLower().Contains(normalizedSearch) ||
                (c.LegalName != null && c.LegalName.ToLower().Contains(normalizedSearch)) ||
                (c.TradeName != null && c.TradeName.ToLower().Contains(normalizedSearch)));
        }

        var total = await clientsQuery.CountAsync(cancellationToken);
        var clients = await clientsQuery
            .OrderBy(c => c.AccountName)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(limit, 1, 100))
            .Select(c => new { c.Id, c.TenantId, c.AccountName })
            .ToListAsync(cancellationToken);
        var clientIds = clients.Select(c => c.Id).ToList();
        var tenantIds = clients.Select(c => c.TenantId).Distinct().ToList();
        var grants = await _db.MarketplaceOAuthGrants.AsNoTracking()
            .Where(x => clientIds.Contains(x.ClientId) && x.AppFamily == "MERCADO_PAGO")
            .OrderByDescending(x => x.UpdatedAt)
            .ToListAsync(cancellationToken);
        var tenantSlugs = await _db.Tenants.AsNoTracking()
            .Where(t => tenantIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Slug })
            .ToDictionaryAsync(t => t.Id, t => t.Slug, cancellationToken);
        var grantMap = grants.GroupBy(x => x.ClientId).ToDictionary(x => x.Key, x => x.First());
        var now = DateTimeOffset.UtcNow;

        return Ok(new PagedIntegrationClientsResult
        {
            Total = total,
            Items = clients.Select(client =>
            {
                grantMap.TryGetValue(client.Id, out var grant);
                tenantSlugs.TryGetValue(client.TenantId, out var tenantSlug);
                var connected = grant != null && !grant.RequiresReauthorization &&
                    (grant.TokenExpiresAt > now || grant.RefreshTokenProtected != "");
                var billingVerified = connected && grant!.LastCapabilityVerifiedAt.HasValue &&
                    grant.CapabilitiesJson.Contains("\"billingMercadoPago\":true", StringComparison.OrdinalIgnoreCase);
                return new IntegrationClientResult
                {
                    ClientId = client.Id,
                    TenantId = client.TenantId,
                    TenantSlug = tenantSlug ?? string.Empty,
                    ClientName = client.AccountName,
                    IsConnected = connected,
                    ConnectedAt = grant?.CreatedAt.UtcDateTime,
                    LastSyncAt = grant?.LastCapabilityVerifiedAt?.UtcDateTime,
                    SellerOrCompanyInfo = grant == null
                        ? "Autorização pendente"
                        : $"Seller {grant.SellerId} · {(billingVerified ? "Billing verificado" : grant.RequiresReauthorization ? "Reautorização necessária" : grant.CapabilityError ?? "Billing pendente")}",
                    HealthStatus = grant == null ? "NOT_CONNECTED" : billingVerified ? "VERIFIED" : grant.RequiresReauthorization ? "REAUTH_REQUIRED" : "PENDING"
                };
            }).ToList()
        });
    }

    [HttpGet("shopee/clients")]
    public async Task<IActionResult> GetShopeeClients(
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        var result = await GetClientsByProvider(MarketplaceProvider.Shopee, skip, limit, search, cancellationToken);
        return Ok(result);
    }

    [HttpDelete("mercadolivre/clients/{clientId:guid}")]
    public Task<IActionResult> DisconnectMercadoLivre([FromRoute] Guid clientId, CancellationToken cancellationToken)
    {
        return DisconnectByProvider(MarketplaceProvider.MercadoLivre, clientId, cancellationToken);
    }

    [HttpDelete("tinyerp/clients/{clientId:guid}")]
    public Task<IActionResult> DisconnectTinyErp([FromRoute] Guid clientId, CancellationToken cancellationToken)
    {
        return DisconnectByProvider(MarketplaceProvider.TinyErp, clientId, cancellationToken);
    }

    [HttpDelete("shopify/clients/{clientId:guid}")]
    public Task<IActionResult> DisconnectShopify([FromRoute] Guid clientId, CancellationToken cancellationToken)
    {
        return DisconnectByProvider(MarketplaceProvider.Shopify, clientId, cancellationToken);
    }

    [HttpDelete("tiktokshop/clients/{clientId:guid}")]
    public Task<IActionResult> DisconnectTikTokShop([FromRoute] Guid clientId, CancellationToken cancellationToken)
    {
        return DisconnectByProvider(MarketplaceProvider.TikTokShop, clientId, cancellationToken);
    }

    [HttpDelete("shopee/clients/{clientId:guid}")]
    public Task<IActionResult> DisconnectShopee([FromRoute] Guid clientId, CancellationToken cancellationToken)
    {
        return DisconnectByProvider(MarketplaceProvider.Shopee, clientId, cancellationToken);
    }

    private static IntegrationCardResult BuildIntegrationCard(
        MarketplaceProvider provider,
        string name,
        string description,
        IReadOnlyDictionary<MarketplaceProvider, int> counts)
    {
        return new IntegrationCardResult
        {
            Provider = (int)provider,
            Slug = provider.ToString().ToLowerInvariant(),
            Category = "Operacional",
            Name = name,
            Description = description,
            ConnectedCount = counts.TryGetValue(provider, out var count) ? count : 0
        };
    }

    private async Task<IActionResult> DisconnectByProvider(
        MarketplaceProvider provider,
        Guid clientId,
        CancellationToken cancellationToken)
    {
        var rows = await _db.TenantMarketplaceConnections
            .Where(c => c.ClientId == clientId && c.Provider == provider)
            .ToListAsync(cancellationToken);

        _db.TenantMarketplaceConnections.RemoveRange(rows);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok();
    }

    private async Task<PagedIntegrationClientsResult> GetClientsByProvider(
        MarketplaceProvider provider,
        int skip,
        int limit,
        string? search,
        CancellationToken cancellationToken)
    {
        var clientsQuery = _db.Clients.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalizedSearch = search.Trim().ToLower();
            clientsQuery = clientsQuery.Where(c =>
                c.AccountName.ToLower().Contains(normalizedSearch) ||
                (c.LegalName != null && c.LegalName.ToLower().Contains(normalizedSearch)) ||
                (c.TradeName != null && c.TradeName.ToLower().Contains(normalizedSearch)));
        }

        var total = await clientsQuery.CountAsync(cancellationToken);
        var clients = await clientsQuery
            .OrderBy(c => c.AccountName)
            .Skip(skip)
            .Take(limit)
            .Select(c => new { c.Id, c.TenantId, c.AccountName })
            .ToListAsync(cancellationToken);

        var clientIds = clients.Select(c => c.Id).ToList();
        var tenantIds = clients.Select(c => c.TenantId).Distinct().ToList();

        var connections = await _db.TenantMarketplaceConnections
            .Where(c => c.Provider == provider && clientIds.Contains(c.ClientId))
            .Select(c => new { c.ClientId, c.Nickname, c.CreatedAt, c.LastSyncAt })
            .ToListAsync(cancellationToken);

        var tenantSlugs = await _db.Tenants
            .Where(t => tenantIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Slug })
            .ToDictionaryAsync(t => t.Id, t => t.Slug, cancellationToken);

        var connectionMap = connections
            .GroupBy(c => c.ClientId)
            .ToDictionary(g => g.Key, g => g.First());

        var items = clients.Select(c =>
        {
            connectionMap.TryGetValue(c.Id, out var connection);
            tenantSlugs.TryGetValue(c.TenantId, out var slug);

            return new IntegrationClientResult
            {
                ClientId = c.Id,
                TenantId = c.TenantId,
                TenantSlug = slug ?? string.Empty,
                ClientName = c.AccountName,
                IsConnected = connection != null,
                ConnectedAt = connection?.CreatedAt.UtcDateTime,
                LastSyncAt = connection?.LastSyncAt?.UtcDateTime,
                SellerOrCompanyInfo = connection?.Nickname
            };
        }).ToList();

        return new PagedIntegrationClientsResult
        {
            Items = items,
            Total = total
        };
    }
}
