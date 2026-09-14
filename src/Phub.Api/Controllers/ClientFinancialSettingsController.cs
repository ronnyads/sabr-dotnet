using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Domain.Enums;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/client/financial-settings")]
public sealed class ClientFinancialSettingsController : ControllerBase
{
    private readonly ITenantProvider _tenant;
    private readonly FinancialProfitabilityService _service;
    public ClientFinancialSettingsController(ITenantProvider tenant, FinancialProfitabilityService service)
    { _tenant = tenant; _service = service; }

    [HttpGet("tax")]
    public async Task<IActionResult> Get([FromQuery] long sellerId, CancellationToken ct)
    {
        if (!TryContext(out var tenantId, out var clientId)) return Forbid();
        var result = await _service.GetTaxAsync(tenantId!, clientId, sellerId, ct);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPut("tax")]
    public async Task<IActionResult> Put([FromBody] UpdateSellerTaxProfileRequest request, CancellationToken ct)
    {
        if (!TryContext(out var tenantId, out var clientId)) return Forbid();
        try
        {
            return Ok(await _service.UpdateTaxAsync(tenantId!, clientId, request,
                User.FindFirst("sub")?.Value ?? User.Identity?.Name ?? "client", ct));
        }
        catch (ArgumentException ex) { return BadRequest(new ApiError { Code = "TAX_INVALID", Message = ex.Message, TraceId = HttpContext.TraceIdentifier }); }
        catch (InvalidOperationException ex) { return Conflict(new ApiError { Code = "SELLER_INVALID", Message = ex.Message, TraceId = HttpContext.TraceIdentifier }); }
    }

    private bool TryContext(out string? tenantId, out Guid clientId)
    {
        tenantId = _tenant.TenantId;
        clientId = Guid.Empty;
        return string.Equals(User.FindFirst("accountType")?.Value, AccountTypes.Client, StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(tenantId)
               && Guid.TryParse(User.FindFirst("clientId")?.Value, out clientId);
    }
}
