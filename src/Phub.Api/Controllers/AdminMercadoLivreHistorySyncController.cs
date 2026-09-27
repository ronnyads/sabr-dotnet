using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phub.Application.Abstractions;
using Phub.Application.Services;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/integrations/mercadolivre/history-sync")]
public sealed class AdminMercadoLivreHistorySyncController : ControllerBase
{
    private readonly ITenantProvider _tenant;
    private readonly FinancialSyncJobService _jobs;

    public AdminMercadoLivreHistorySyncController(ITenantProvider tenant, FinancialSyncJobService jobs)
    {
        _tenant = tenant;
        _jobs = jobs;
    }

    [HttpPost("{jobId:guid}/retry-gaps")]
    public async Task<IActionResult> RetryGaps(Guid jobId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_tenant.TenantId)) return BadRequest();
        try
        {
            return Accepted(await _jobs.RetryHistoryGapsAsync(_tenant.TenantId, jobId, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { code = "ML_HISTORY_GAPS_NOT_RETRYABLE", message = ex.Message });
        }
    }
}
