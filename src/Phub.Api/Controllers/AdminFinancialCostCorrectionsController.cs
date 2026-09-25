using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phub.Application.Models;
using Phub.Application.Services;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize(Roles = "SuperAdmin")]
[Route("api/v1/admin/clients/{clientId:guid}/financial-cost-corrections")]
public sealed class AdminFinancialCostCorrectionsController : ControllerBase
{
    private readonly FinancialCostCorrectionPlanService _service;
    public AdminFinancialCostCorrectionsController(FinancialCostCorrectionPlanService service) => _service = service;

    [HttpPost("dry-run")]
    public async Task<IActionResult> DryRun(Guid clientId, [FromBody] FinancialCostCorrectionDryRunRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = User.FindFirst("tenant_id")?.Value ?? User.FindFirst("tenantId")?.Value;
        var actorValue = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(tenantId) || !Guid.TryParse(actorValue, out var actorId)) return Unauthorized();
        return Ok(await _service.DryRunAsync(tenantId, clientId, request, actorId, cancellationToken));
    }

    [HttpGet("{planId:guid}")]
    [HttpGet("{planId:guid}/report")]
    public async Task<IActionResult> Get(Guid clientId, Guid planId, CancellationToken cancellationToken)
    {
        var tenantId = User.FindFirst("tenant_id")?.Value ?? User.FindFirst("tenantId")?.Value;
        if (string.IsNullOrWhiteSpace(tenantId)) return Unauthorized();
        var result = await _service.GetAsync(tenantId, clientId, planId, cancellationToken);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPost("{planId:guid}/approve")]
    public async Task<IActionResult> Approve(Guid clientId, Guid planId,
        [FromBody] FinancialCorrectionPlanCommand command, CancellationToken cancellationToken)
    {
        if (!TryScope(out var tenantId, out var actorId)) return Unauthorized();
        return Ok(await _service.ApproveAsync(tenantId, clientId, planId, command, actorId, cancellationToken));
    }

    [HttpPost("{planId:guid}/resume")]
    public async Task<IActionResult> Resume(Guid clientId, Guid planId,
        [FromBody] FinancialCorrectionPlanCommand command, CancellationToken cancellationToken)
    {
        if (!TryScope(out var tenantId, out var actorId)) return Unauthorized();
        return Ok(await _service.ResumeAsync(tenantId, clientId, planId, command, actorId, cancellationToken));
    }

    [HttpPost("{planId:guid}/activate")]
    public async Task<IActionResult> Activate(Guid clientId, Guid planId,
        [FromBody] FinancialCorrectionPlanCommand command, CancellationToken cancellationToken)
    {
        if (!TryScope(out var tenantId, out var actorId)) return Unauthorized();
        return Ok(await _service.ActivateAsync(tenantId, clientId, planId, command, actorId, cancellationToken));
    }

    private bool TryScope(out string tenantId, out Guid actorId)
    {
        actorId = Guid.Empty;
        tenantId = User.FindFirst("tenant_id")?.Value ?? User.FindFirst("tenantId")?.Value ?? string.Empty;
        var actorValue = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        return !string.IsNullOrWhiteSpace(tenantId) && Guid.TryParse(actorValue, out actorId);
    }
}
