using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Domain.Enums;

namespace Sabr.Api.Controllers;

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

    // GET /api/v1/admin/integrations
    [HttpGet]
    public async Task<IActionResult> GetIntegrations(CancellationToken cancellationToken)
    {
        try
        {
            var connections = await _db.TenantMarketplaceConnections
                .GroupBy(c => c.Provider)
                .Select(g => new { Provider = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);

            var mlCount      = connections.FirstOrDefault(c => c.Provider == MarketplaceProvider.MercadoLivre)?.Count ?? 0;
            var tinyCount    = connections.FirstOrDefault(c => c.Provider == MarketplaceProvider.TinyErp)?.Count ?? 0;
            var shopifyCount = connections.FirstOrDefault(c => c.Provider == MarketplaceProvider.Shopify)?.Count ?? 0;

            var result = new List<IntegrationCardResult>
            {
                new()
                {
                    Provider     = (int)MarketplaceProvider.MercadoLivre,
                    Name         = "Mercado Livre",
                    Description  = "Sincronize pedidos e produtos com o Mercado Livre.",
                    ConnectedCount = mlCount
                },
                new()
                {
                    Provider     = (int)MarketplaceProvider.TinyErp,
                    Name         = "Tiny ERP",
                    Description  = "Integre pedidos e emissao de notas fiscais com o Tiny ERP.",
                    ConnectedCount = tinyCount
                },
                new()
                {
                    Provider     = (int)MarketplaceProvider.Shopify,
                    Name         = "Shopify",
                    Description  = "Sincronize pedidos e inventário com a loja Shopify.",
                    ConnectedCount = shopifyCount
                }
            };

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin: failed to load integrations hub. traceId={TraceId}", HttpContext.TraceIdentifier);
            return StatusCode(500, new { code = "INTEGRATIONS_HUB_ERROR", message = "Falha ao carregar integrações." });
        }
    }

    // GET /api/v1/admin/integrations/mercadolivre/clients
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

    // GET /api/v1/admin/integrations/tinyerp/clients
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

    // DELETE /api/v1/admin/integrations/mercadolivre/clients/{clientId}
    [HttpDelete("mercadolivre/clients/{clientId:guid}")]
    public async Task<IActionResult> DisconnectMercadoLivre([FromRoute] Guid clientId, CancellationToken cancellationToken)
    {
        var rows = await _db.TenantMarketplaceConnections
            .Where(c => c.ClientId == clientId && c.Provider == MarketplaceProvider.MercadoLivre)
            .ToListAsync(cancellationToken);
        _db.TenantMarketplaceConnections.RemoveRange(rows);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok();
    }

    // DELETE /api/v1/admin/integrations/tinyerp/clients/{clientId}
    [HttpDelete("tinyerp/clients/{clientId:guid}")]
    public async Task<IActionResult> DisconnectTinyErp([FromRoute] Guid clientId, CancellationToken cancellationToken)
    {
        var rows = await _db.TenantMarketplaceConnections
            .Where(c => c.ClientId == clientId && c.Provider == MarketplaceProvider.TinyErp)
            .ToListAsync(cancellationToken);
        _db.TenantMarketplaceConnections.RemoveRange(rows);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
            var s = search.Trim().ToLower();
            clientsQuery = clientsQuery.Where(c =>
                c.AccountName.ToLower().Contains(s) ||
                (c.LegalName  != null && c.LegalName.ToLower().Contains(s)) ||
                (c.TradeName  != null && c.TradeName.ToLower().Contains(s)));
        }

        var total   = await clientsQuery.CountAsync(cancellationToken);
        var clients = await clientsQuery
            .OrderBy(c => c.AccountName)
            .Skip(skip)
            .Take(limit)
            .Select(c => new { c.Id, c.TenantId, c.AccountName })
            .ToListAsync(cancellationToken);

        var clientIds   = clients.Select(c => c.Id).ToList();
        var tenantIds   = clients.Select(c => c.TenantId).Distinct().ToList();

        var connections = await _db.TenantMarketplaceConnections
            .Where(c => c.Provider == provider && clientIds.Contains(c.ClientId))
            .Select(c => new { c.ClientId, c.Nickname, c.CreatedAt, c.LastSyncAt })
            .ToListAsync(cancellationToken);

        var tenantSlugs = await _db.Tenants
            .Where(t => tenantIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Slug })
            .ToDictionaryAsync(t => t.Id, t => t.Slug, cancellationToken);

        var connMap = connections
            .GroupBy(c => c.ClientId)
            .ToDictionary(g => g.Key, g => g.First());

        var items = clients.Select(c =>
        {
            connMap.TryGetValue(c.Id, out var conn);
            tenantSlugs.TryGetValue(c.TenantId, out var slug);
            return new IntegrationClientResult
            {
                ClientId             = c.Id,
                TenantId             = c.TenantId,
                TenantSlug           = slug ?? "",
                ClientName           = c.AccountName,
                IsConnected          = conn != null,
                ConnectedAt          = conn?.CreatedAt.UtcDateTime,
                LastSyncAt           = conn?.LastSyncAt?.UtcDateTime,
                SellerOrCompanyInfo  = conn?.Nickname
            };
        }).ToList();

        return new PagedIntegrationClientsResult { Items = items, Total = total };
    }
}
