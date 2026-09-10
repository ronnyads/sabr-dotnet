using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Domain.Enums;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Analyst,Admin,SuperAdmin")]
[Route("api/v1/admin/sentinel")]
public sealed class AdminSentinelController : ControllerBase
{
    private readonly SentinelService _sentinel;
    public AdminSentinelController(SentinelService sentinel) => _sentinel = sentinel;

    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] string? tenantId, [FromQuery] Guid? clientId,
        [FromQuery] long? sellerId, [FromQuery] string? provider, [FromQuery] string? stage,
        [FromQuery] string? risk, [FromQuery] string? cause, [FromQuery] string? @operator,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct)
        => Ok(await _sentinel.SummaryAsync(Filter(tenantId, clientId, sellerId, provider, stage, risk, cause, @operator, from, to, 0, 5000), ct));

    [HttpGet("shipments")]
    public async Task<IActionResult> Shipments([FromQuery] string? tenantId, [FromQuery] Guid? clientId,
        [FromQuery] long? sellerId, [FromQuery] string? provider, [FromQuery] string? stage,
        [FromQuery] string? risk, [FromQuery] string? cause, [FromQuery] string? @operator,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] int skip = 0, [FromQuery] int limit = 100, CancellationToken ct = default)
        => Ok(await _sentinel.ListAsync(Filter(tenantId, clientId, sellerId, provider, stage, risk, cause, @operator, from, to, skip, limit), ct));

    [HttpGet("shipments/{shipmentId}")]
    public async Task<IActionResult> Shipment(string shipmentId, CancellationToken ct)
    {
        var result = await _sentinel.DetailAsync(shipmentId, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("risk-policy")]
    public async Task<IActionResult> Policy(CancellationToken ct) => Ok(await _sentinel.GetPolicyAsync(ct));

    [HttpPut("risk-policy")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> UpdatePolicy([FromBody] SentinelRiskPolicyUpdateRequest request, CancellationToken ct)
    {
        try { return Ok(await _sentinel.UpdatePolicyAsync(request, ct)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    private static SentinelShipmentFilter Filter(string? tenantId, Guid? clientId, long? sellerId, string? provider,
        string? stage, string? risk, string? cause, string? @operator, DateTimeOffset? from, DateTimeOffset? to, int skip, int limit)
        => new(tenantId, clientId, sellerId,
            Enum.TryParse<MarketplaceProvider>(provider, true, out var parsed) ? parsed : null,
            stage, risk, cause, @operator, from, to, skip, limit);
}
