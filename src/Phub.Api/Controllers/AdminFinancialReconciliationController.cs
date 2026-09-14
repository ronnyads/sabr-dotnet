using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/financial-reconciliation")]
public sealed class AdminFinancialReconciliationController : ControllerBase
{
    private readonly ITenantProvider _tenant;
    private readonly IAppDbContext _db;

    public AdminFinancialReconciliationController(ITenantProvider tenant, IAppDbContext db)
    {
        _tenant = tenant;
        _db = db;
    }

    [HttpGet("runs")]
    public async Task<IActionResult> GetRuns([FromQuery] long? sellerId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_tenant.TenantId)) return BadRequest();
        var query = _db.FinancialSyncJobs.AsNoTracking().Where(x =>
            x.TenantId == _tenant.TenantId && (!sellerId.HasValue || x.SellerId == sellerId.Value));
        var jobs = await query.OrderByDescending(x => x.CreatedAt).Take(100).Select(x => new
        {
            jobId = x.Id, x.ClientId, x.SellerId, provider = x.Provider.ToString(), x.JobType, x.Status,
            x.RangeFrom, x.RangeTo, x.Checkpoint, x.Total, x.Processed, x.Attempts, x.NextAttemptAt,
            x.LockedBy, x.LeaseUntil, x.LastError, x.CreatedAt, x.UpdatedAt, x.CompletedAt
        }).ToListAsync(cancellationToken);
        return Ok(jobs);
    }
}
