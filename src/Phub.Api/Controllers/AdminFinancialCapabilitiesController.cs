using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Services;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/tenants/{tenantSlug}/clients/{clientId:guid}/integrations/mercadolivre/financial-capabilities")]
public sealed class AdminFinancialCapabilitiesController : ControllerBase
{
    private readonly IAppDbContext _dbContext;
    private readonly FinancialCapabilityService _service;

    public AdminFinancialCapabilitiesController(IAppDbContext dbContext, FinancialCapabilityService service)
    {
        _dbContext = dbContext;
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> Get(string tenantSlug, Guid clientId, CancellationToken ct)
        => await ExecuteAsync(tenantSlug, clientId, false, ct);

    [HttpPost("probe")]
    public async Task<IActionResult> Probe(string tenantSlug, Guid clientId, CancellationToken ct)
        => await ExecuteAsync(tenantSlug, clientId, true, ct);

    private async Task<IActionResult> ExecuteAsync(
        string tenantSlug,
        Guid clientId,
        bool probe,
        CancellationToken cancellationToken)
    {
        var normalizedSlug = tenantSlug.Trim().ToLowerInvariant();
        var tenantId = await _dbContext.Tenants
            .AsNoTracking()
            .Where(x => x.Slug == normalizedSlug)
            .Select(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return NotFound(CreateApiError("TENANT_NOT_FOUND", "Tenant not found"));
        }

        var connectionExists = await _dbContext.TenantMarketplaceConnections
            .AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId && x.ClientId == clientId, cancellationToken);
        if (!connectionExists)
        {
            return NotFound(CreateApiError("CONNECTION_NOT_FOUND", "Mercado Livre connection not found"));
        }

        return Ok(await _service.GetAsync(tenantId, clientId, probe, cancellationToken));
    }

    private ApiError CreateApiError(string code, string message) => new()
    {
        Code = code,
        Message = message,
        TraceId = HttpContext.TraceIdentifier
    };
}
