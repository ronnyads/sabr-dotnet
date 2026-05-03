using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Phub.Domain.Enums;
using Phub.Infrastructure.Persistence;

namespace Phub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,SuperAdmin")]
[Route("api/v1/admin/integration")]
public sealed class AdminIntegrationController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public AdminIntegrationController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("outbox")]
    public async Task<IActionResult> ListOutbox(
        [FromQuery] string? tenantId = null,
        [FromQuery] string? status = null,
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0)
        {
            return BadRequest(new { errors = new[] { new { field = "skip", message = "Skip must be 0 or greater" } } });
        }

        if (limit <= 0 || limit > 200)
        {
            return BadRequest(new { errors = new[] { new { field = "limit", message = "Limit must be between 1 and 200" } } });
        }

        OutboxStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<OutboxStatus>(status, true, out var statusValue))
            {
                return BadRequest(new { errors = new[] { new { field = "status", message = "Invalid status" } } });
            }

            parsedStatus = statusValue;
        }

        var query = _dbContext.ProtheusOutboxEvents.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            query = query.Where(o => o.TenantId == tenantId);
        }

        if (parsedStatus.HasValue)
        {
            query = query.Where(o => o.Status == parsedStatus.Value);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip(skip)
            .Take(limit)
            .Select(o => new
            {
                o.Id,
                o.TenantId,
                o.AggregateType,
                o.AggregateId,
                o.EventType,
                Status = o.Status.ToString(),
                o.Attempts,
                o.NextRetryAt,
                o.CorrelationId,
                o.LastError,
                o.CreatedAt,
                o.ProcessedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(new { items, total, skip, limit });
    }

    [HttpGet("outbox/{id:guid}")]
    public async Task<IActionResult> GetOutbox([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var item = await _dbContext.ProtheusOutboxEvents
            .AsNoTracking()
            .Where(o => o.Id == id)
            .Select(o => new
            {
                o.Id,
                o.TenantId,
                o.AggregateType,
                o.AggregateId,
                o.EventType,
                Status = o.Status.ToString(),
                o.PayloadJson,
                o.Attempts,
                o.NextRetryAt,
                o.CorrelationId,
                o.LastError,
                o.CreatedAt,
                o.UpdatedAt,
                o.ProcessedAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (item == null)
        {
            return NotFound(new { error = "Outbox event not found" });
        }

        return Ok(item);
    }
}
