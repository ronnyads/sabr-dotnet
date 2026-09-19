using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class FinancialSyncJobService
{
    private readonly IAppDbContext _db;
    private readonly MercadoLivreSyncService _sync;

    public FinancialSyncJobService(IAppDbContext db, MercadoLivreSyncService sync)
    {
        _db = db;
        _sync = sync;
    }

    public async Task<FinancialSyncEnqueueResult> EnqueueOperationalBackfillAsync(
        string tenantId, Guid clientId, long? sellerId, int lookbackDays = 365, int chunkDays = 30,
        CancellationToken cancellationToken = default)
    {
        lookbackDays = Math.Clamp(lookbackDays, 1, 365);
        chunkDays = Math.Clamp(chunkDays, 1, 31);
        var sellers = await _db.TenantMarketplaceConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientId == clientId && x.Provider == MarketplaceProvider.MercadoLivre)
            .Where(x => !sellerId.HasValue || x.SellerId == sellerId.Value)
            .Select(x => x.SellerId).Distinct().ToListAsync(cancellationToken);
        if (sellers.Count == 0) throw new InvalidOperationException("Nenhum seller Mercado Livre conectado.");

        var result = new FinancialSyncEnqueueResult();
        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-lookbackDays);
        foreach (var seller in sellers)
        {
            var active = await _db.FinancialSyncJobs.AsNoTracking().FirstOrDefaultAsync(x =>
                x.TenantId == tenantId && x.ClientId == clientId && x.Provider == MarketplaceProvider.MercadoLivre
                && x.SellerId == seller && x.JobType == FinancialSyncJobTypes.OperationalSyncBatch
                && (x.Status == "PENDING" || x.Status == "RUNNING" || x.Status == "RETRY"), cancellationToken);
            if (active != null)
            {
                result.Jobs.Add(Map(active));
                continue;
            }
            var batch = new FinancialSyncJob
            {
                TenantId = tenantId, ClientId = clientId, Provider = MarketplaceProvider.MercadoLivre, SellerId = seller,
                JobType = FinancialSyncJobTypes.OperationalSyncBatch, RangeFrom = from, RangeTo = to,
                DedupeKey = $"OP:BATCH:{tenantId}:{clientId:N}:{seller}:{from:yyyyMMddHH}:{to:yyyyMMddHH}", Status = "PENDING"
            };
            _db.FinancialSyncJobs.Add(batch);
            for (var cursor = from; cursor < to; cursor = cursor.AddDays(chunkDays))
            {
                var chunkTo = cursor.AddDays(chunkDays) < to ? cursor.AddDays(chunkDays) : to;
                _db.FinancialSyncJobs.Add(new FinancialSyncJob
                {
                    ParentJobId = batch.Id, TenantId = tenantId, ClientId = clientId, Provider = MarketplaceProvider.MercadoLivre,
                    SellerId = seller, JobType = FinancialSyncJobTypes.OperationalSyncChunk, RangeFrom = cursor, RangeTo = chunkTo,
                    DedupeKey = $"OP:CHUNK:{tenantId}:{clientId:N}:{seller}:{cursor:O}:{chunkTo:O}", Status = "PENDING",
                    Checkpoint = cursor.ToString("O")
                });
                batch.Total++;
            }
            result.Jobs.Add(Map(batch));
        }
        await _db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<FinancialSyncJobResult?> GetAsync(string tenantId, Guid clientId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _db.FinancialSyncJobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == jobId
            && x.TenantId == tenantId && x.ClientId == clientId, cancellationToken);
        return job == null ? null : Map(job);
    }

    public async Task<IReadOnlyList<FinancialSyncJobResult>> GetStatusAsync(string tenantId, Guid clientId, CancellationToken cancellationToken)
        => await _db.FinancialSyncJobs.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClientId == clientId
                && x.JobType == FinancialSyncJobTypes.OperationalSyncBatch)
            .OrderByDescending(x => x.CreatedAt).Take(10).Select(x => new FinancialSyncJobResult
            {
                JobId = x.Id, SellerId = x.SellerId, JobType = x.JobType, Status = x.Status,
                RangeFrom = x.RangeFrom, RangeTo = x.RangeTo, Total = x.Total, Processed = x.Processed,
                LastError = x.LastError, CreatedAt = x.CreatedAt, CompletedAt = x.CompletedAt
            }).ToListAsync(cancellationToken);

    public async Task<bool> ProcessNextAsync(string workerId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        FinancialSyncJob? job;
        await using (var transaction = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(cancellationToken) : null)
        {
            var query = _db.FinancialSyncJobs.Where(x => x.JobType == FinancialSyncJobTypes.OperationalSyncChunk
                && (x.Status == "PENDING" || x.Status == "RETRY" || x.Status == "RUNNING")
                && (!x.NextAttemptAt.HasValue || x.NextAttemptAt <= now)
                && (!x.LeaseUntil.HasValue || x.LeaseUntil < now)).OrderBy(x => x.CreatedAt);
            job = _db.Database.IsRelational()
                ? await _db.FinancialSyncJobs.FromSqlRaw("SELECT * FROM financial_sync_jobs WHERE job_type = 'OPERATIONAL_SYNC_CHUNK' AND status IN ('PENDING','RETRY','RUNNING') AND (next_attempt_at IS NULL OR next_attempt_at <= now()) AND (lease_until IS NULL OR lease_until < now()) ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1").FirstOrDefaultAsync(cancellationToken)
                : await query.FirstOrDefaultAsync(cancellationToken);
            if (job == null) return false;
            job.Status = "RUNNING";
            job.LockedBy = workerId;
            // A 30-day chunk can fetch many orders and shipments. Keep the
            // lease longer than one normal chunk, while allowing a crashed
            // worker's RUNNING job to be reclaimed after expiry.
            job.LeaseUntil = now.AddMinutes(30);
            job.Attempts++;
            job.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
        }

        try
        {
            var syncResult = await _sync.SyncRangeNowAsync(job.TenantId, job.ClientId, job.SellerId,
                job.RangeFrom, job.RangeTo, cancellationToken);
            if (!syncResult.Succeeded)
                throw new InvalidOperationException(string.Join("; ", syncResult.Errors.Select(x => x.Message)));
            job.Status = "COMPLETED";
            job.Processed = 1;
            job.Total = 1;
            job.Checkpoint = job.RangeTo.ToString("O");
            job.ResultJson = System.Text.Json.JsonSerializer.Serialize(syncResult.Data);
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed sync may leave invalid order entities tracked. Persisting the
            // retry status with that same change tracker would replay the failed write.
            if (_db is DbContext context)
            {
                var jobId = job.Id;
                context.ChangeTracker.Clear();
                job = await _db.FinancialSyncJobs.SingleAsync(x => x.Id == jobId, cancellationToken);
            }
            job.Status = job.Attempts >= 8 ? "FAILED" : "RETRY";
            var exponent = Math.Min(job.Attempts, 5);
            var ceilingSeconds = Math.Min(900, 30 * (1 << exponent));
            job.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Random.Shared.Next(30, ceilingSeconds + 1));
            job.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
        }
        finally
        {
            job.LockedBy = null;
            job.LeaseUntil = null;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            await UpdateParentAsync(job.ParentJobId, cancellationToken);
        }
        return true;
    }

    private async Task UpdateParentAsync(Guid? parentId, CancellationToken ct)
    {
        if (!parentId.HasValue) return;
        var parent = await _db.FinancialSyncJobs.FirstOrDefaultAsync(x => x.Id == parentId.Value, ct);
        if (parent == null) return;
        var children = await _db.FinancialSyncJobs.AsNoTracking().Where(x => x.ParentJobId == parent.Id).ToListAsync(ct);
        parent.Processed = children.Count(x => x.Status == "COMPLETED");
        parent.Total = children.Count;
        parent.Status = children.Any(x => x.Status == "FAILED") ? "FAILED"
            : children.All(x => x.Status == "COMPLETED") ? "COMPLETED" : "RUNNING";
        parent.LastError = children.FirstOrDefault(x => x.Status == "FAILED")?.LastError;
        parent.CompletedAt = parent.Status is "COMPLETED" or "FAILED" ? DateTimeOffset.UtcNow : null;
        parent.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private static FinancialSyncJobResult Map(FinancialSyncJob x) => new()
    {
        JobId = x.Id, SellerId = x.SellerId, JobType = x.JobType, Status = x.Status,
        RangeFrom = x.RangeFrom, RangeTo = x.RangeTo, Total = x.Total, Processed = x.Processed,
        LastError = x.LastError, CreatedAt = x.CreatedAt, CompletedAt = x.CompletedAt
    };
}
