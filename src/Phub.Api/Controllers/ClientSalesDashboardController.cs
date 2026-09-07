using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Domain.Enums;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/client/dashboard")]
public sealed class ClientSalesDashboardController : ControllerBase
{
    private readonly ITenantProvider _tenantProvider;
    private readonly ClientSalesDashboardService _dashboardService;

    public ClientSalesDashboardController(
        ITenantProvider tenantProvider,
        ClientSalesDashboardService dashboardService)
    {
        _tenantProvider = tenantProvider;
        _dashboardService = dashboardService;
    }

    [HttpGet("sales")]
    public async Task<IActionResult> GetSales(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? provider = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(User.FindFirst("accountType")?.Value, AccountTypes.Client, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        var tenantId = _tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new ApiError { Code = "TENANT_NOT_RESOLVED", Message = "Tenant not resolved", TraceId = HttpContext.TraceIdentifier });
        }

        if (!Guid.TryParse(User.FindFirst("clientId")?.Value, out var clientId))
        {
            return Unauthorized(new ApiError { Code = "INVALID_CLIENT_CONTEXT", Message = "Invalid client context", TraceId = HttpContext.TraceIdentifier });
        }

        MarketplaceProvider? parsedProvider = null;
        if (!string.IsNullOrWhiteSpace(provider))
        {
            if (!Enum.TryParse<MarketplaceProvider>(provider, true, out var value))
            {
                return BadRequest(new ApiError { Code = "PROVIDER_INVALID", Message = "Canal de venda inválido", TraceId = HttpContext.TraceIdentifier });
            }

            parsedProvider = value;
        }

        var result = await _dashboardService.GetAsync(tenantId, clientId, from, to, parsedProvider, cancellationToken);
        return Ok(result);
    }
}
