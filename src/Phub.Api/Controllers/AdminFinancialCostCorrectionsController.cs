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
}
