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
    private readonly FinancialProfitabilityService _profitabilityService;
    private readonly FinancialSyncJobService _financialSync;

    public ClientSalesDashboardController(
        ITenantProvider tenantProvider,
        ClientSalesDashboardService dashboardService,
        FinancialProfitabilityService profitabilityService,
        FinancialSyncJobService financialSync)
    {
        _tenantProvider = tenantProvider;
        _dashboardService = dashboardService;
        _profitabilityService = profitabilityService;
        _financialSync = financialSync;
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

    [HttpGet("profitability")]
    public async Task<IActionResult> GetProfitability([FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? provider = null, [FromQuery] long? sellerId = null, CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error)) return error!;
        MarketplaceProvider? parsed = null;
        if (!string.IsNullOrWhiteSpace(provider))
        {
            if (!Enum.TryParse<MarketplaceProvider>(provider, true, out var value)) return BadRequest(new ApiError
            { Code = "PROVIDER_INVALID", Message = "Canal de venda inválido", TraceId = HttpContext.TraceIdentifier });
            parsed = value;
        }
        return Ok(await _profitabilityService.GetAsync(tenantId!, clientId, from, to, parsed, sellerId, cancellationToken));
    }

    [HttpGet("profitability/orders")]
    public async Task<IActionResult> GetProfitabilityOrders([FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null, [FromQuery] long? sellerId = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error)) return error!;
        return Ok(await _profitabilityService.GetOrdersAsync(tenantId!, clientId, from, to, sellerId, cancellationToken));
    }

    [HttpGet("profitability/orders/{orderId:guid}")]
    public async Task<IActionResult> GetProfitabilityOrder(Guid orderId, CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error)) return error!;
        var result = await _profitabilityService.GetOrderAsync(tenantId!, clientId, orderId, cancellationToken);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPost("sync")]
    public async Task<IActionResult> StartSync([FromQuery] long? sellerId = null, CancellationToken cancellationToken = default)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error)) return error!;
        try
        {
            var result = await _financialSync.EnqueueOperationalBackfillAsync(tenantId!, clientId, sellerId, cancellationToken: cancellationToken);
            return Accepted(result);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ApiError { Code = "SYNC_NOT_AVAILABLE", Message = ex.Message, TraceId = HttpContext.TraceIdentifier });
        }
    }

    [HttpGet("sync/{jobId:guid}")]
    public async Task<IActionResult> GetSync(Guid jobId, CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error)) return error!;
        var result = await _financialSync.GetAsync(tenantId!, clientId, jobId, cancellationToken);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpGet("sync-status")]
    public async Task<IActionResult> GetSyncStatus(CancellationToken cancellationToken)
    {
        if (!TryGetClientContext(out var tenantId, out var clientId, out var error)) return error!;
        return Ok(await _financialSync.GetStatusAsync(tenantId!, clientId, cancellationToken));
    }

    private bool TryGetClientContext(out string? tenantId, out Guid clientId, out IActionResult? error)
    {
        tenantId = _tenantProvider.TenantId;
        clientId = Guid.Empty;
        error = null;
        if (!string.Equals(User.FindFirst("accountType")?.Value, AccountTypes.Client, StringComparison.OrdinalIgnoreCase))
        {
            error = Forbid(); return false;
        }
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            error = BadRequest(new ApiError { Code = "TENANT_NOT_RESOLVED", Message = "Tenant not resolved", TraceId = HttpContext.TraceIdentifier }); return false;
        }
        if (!Guid.TryParse(User.FindFirst("clientId")?.Value, out clientId))
        {
            error = Unauthorized(new ApiError { Code = "INVALID_CLIENT_CONTEXT", Message = "Invalid client context", TraceId = HttpContext.TraceIdentifier }); return false;
        }
        return true;
    }
}
