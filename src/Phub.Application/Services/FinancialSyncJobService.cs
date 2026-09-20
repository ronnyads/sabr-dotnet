using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class FinancialSyncJobService
{
    private readonly IAppDbContext _db;
    private readonly MercadoLivreSyncService _sync;
    private readonly BillingFinancialReconciliationService _billing;
    private readonly ILogger<FinancialSyncJobService> _logger;

    public FinancialSyncJobService(IAppDbContext db, MercadoLivreSyncService sync,
        BillingFinancialReconciliationService billing,
        ILogger<FinancialSyncJobService>? logger = null)
    {
        _db = db;
        _sync = sync;
        _billing = billing;
        _logger = logger ?? NullLogger<FinancialSyncJobService>.Instance;
    }

    public async Task<FinancialSyncEnqueueResult> EnqueueBillingReconciliationAsync(
        string tenantId, Guid clientId, long? sellerId, int lookbackDays = 90,
        CancellationToken cancellationToken = default)
    {
        lookbackDays = Math.Clamp(lookbackDays, 1, 365);
        var from = DateTimeOffset.UtcNow.AddDays(-lookbackDays);
        var query = _db.MarketplaceOrders.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClientId == clientId
            && x.Provider == MarketplaceProvider.MercadoLivre && x.SellerId > 0
            && (x.PaidAt ?? x.ChannelCreatedAt ?? x.ImportedAt) >= from
            && (x.Status == "paid" || x.Status == "partially_refunded" || x.Status == "refunded"));
        if (sellerId.HasValue) query = query.Where(x => x.SellerId == sellerId.Value);
        var rows = await query.OrderBy(x => x.SellerId).ThenBy(x => x.PaidAt ?? x.ImportedAt)
            .Select(x => new { x.SellerId, x.MlOrderId }).ToListAsync(cancellationToken);
        if (rows.Count == 0) throw new InvalidOperationException("Nenhum pedido pago disponível para conferência.");
        var result = new FinancialSyncEnqueueResult();
        foreach (var sellerGroup in rows.GroupBy(x => x.SellerId))
        {
            var batchDedupe = $"BILLING:BATCH:{tenantId}:{clientId:N}:{sellerGroup.Key}:{DateTimeOffset.UtcNow:yyyyMMddHH}";
            var existing = await _db.FinancialSyncJobs.AsNoTracking().FirstOrDefaultAsync(x =>
                x.TenantId == tenantId && x.ClientId == clientId && x.SellerId == sellerGroup.Key
                && x.DedupeKey == batchDedupe, cancellationToken);
            if (existing != null) { result.Jobs.Add(Map(existing)); continue; }
            var parent = new FinancialSyncJob
            {
                TenantId = tenantId, ClientId = clientId, Provider = MarketplaceProvider.MercadoLivre,
                SellerId = sellerGroup.Key, JobType = FinancialSyncJobTypes.BillingReconciliationBatch,
                RangeFrom = from, RangeTo = DateTimeOffset.UtcNow, Status = "PENDING",
                DedupeKey = batchDedupe
            };
            _db.FinancialSyncJobs.Add(parent);
            foreach (var batch in sellerGroup.Select(x => x.MlOrderId).Distinct().Chunk(60))
            {
                var orderPayload = JsonSerializer.Serialize(batch);
                var orderHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(orderPayload))).ToLowerInvariant();
                _db.FinancialSyncJobs.Add(new FinancialSyncJob
                {
                    ParentJobId = parent.Id, TenantId = tenantId, ClientId = clientId,
                    Provider = MarketplaceProvider.MercadoLivre, SellerId = sellerGroup.Key,
                    JobType = FinancialSyncJobTypes.BillingReconciliation, RangeFrom = from,
                    RangeTo = parent.RangeTo, Status = "PENDING",
                    DedupeKey = $"BILLING:ORDERS:{sellerGroup.Key}:{orderHash}",
                    PayloadJson = orderPayload
                });
                parent.Total++;
            }
            result.Jobs.Add(Map(parent));
        }
        await _db.SaveChangesAsync(cancellationToken);
        return result;
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
            var query = _db.FinancialSyncJobs.Where(x => (x.JobType == FinancialSyncJobTypes.OperationalSyncChunk
                    || x.JobType == FinancialSyncJobTypes.BillingReconciliation)
                && (x.Status == "PENDING" || x.Status == "RETRY" || x.Status == "RUNNING")
                && (!x.NextAttemptAt.HasValue || x.NextAttemptAt <= now)
                && (!x.LeaseUntil.HasValue || x.LeaseUntil < now)).OrderBy(x => x.CreatedAt);
            job = _db.Database.IsRelational()
                ? await _db.FinancialSyncJobs.FromSqlRaw("SELECT * FROM financial_sync_jobs WHERE job_type IN ('OPERATIONAL_SYNC_CHUNK','BILLING_RECONCILIATION') AND status IN ('PENDING','RETRY','RUNNING') AND (next_attempt_at IS NULL OR next_attempt_at <= now()) AND (lease_until IS NULL OR lease_until < now()) ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1").FirstOrDefaultAsync(cancellationToken)
                : await query.FirstOrDefaultAsync(cancellationToken);
            if (job == null) return false;
            job.Status = "RUNNING";
            job.LockedBy = workerId;
            // Each claim processes at most six hours. A bounded segment keeps
            // the lease meaningful even for a seller with hundreds of daily
            // orders, while a crashed worker can be reclaimed after expiry.
            job.LeaseUntil = now.AddMinutes(30);
            job.Attempts++;
            job.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
        }

        // From here on, job.LockedBy/LeaseUntil is only a local snapshot: the external
        // sync call below is deliberately unbounded and kept outside any DB transaction,
        // so the 30-minute lease taken above can expire mid-call and another worker can
        // legitimately reclaim and start progressing the same row. Detach job now so no
        // ambient SaveChangesAsync (this method's own, or UpdateParentAsync's) can flush
        // a stale snapshot of it later; the final write at the bottom is instead an
        // explicit "WHERE LockedBy = workerId" guarded update.
        if (_db is DbContext detachContext)
            detachContext.Entry(job).State = EntityState.Detached;

        var segmentFrom = job.RangeFrom;
        if (DateTimeOffset.TryParse(job.Checkpoint, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var checkpoint)
            && checkpoint > job.RangeFrom && checkpoint < job.RangeTo)
            segmentFrom = checkpoint;
        var segmentTo = segmentFrom.AddHours(6) < job.RangeTo ? segmentFrom.AddHours(6) : job.RangeTo;
        _logger.LogInformation("Financial sync chunk claimed job={JobId} seller={SellerId} attempt={Attempt} from={RangeFrom} to={RangeTo}",
            job.Id, job.SellerId, job.Attempts, segmentFrom, segmentTo);
        try
        {
            if (job.JobType == FinancialSyncJobTypes.BillingReconciliation)
            {
                var orderIds = JsonSerializer.Deserialize<string[]>(job.PayloadJson) ?? [];
                var count = await _billing.ReconcileOrdersAsync(job.TenantId, job.ClientId, job.SellerId, orderIds, cancellationToken);
                job.ResultJson = JsonSerializer.Serialize(new { ordersReconciled = count });
                job.Status = "COMPLETED"; job.Processed = 1; job.Total = 1; job.CompletedAt = DateTimeOffset.UtcNow;
                job.LastError = null;
            }
            else
            {
            var syncResult = await _sync.SyncRangeNowAsync(job.TenantId, job.ClientId, job.SellerId,
                segmentFrom, segmentTo, cancellationToken);
            if (!syncResult.Succeeded)
                throw new InvalidOperationException(string.Join("; ", syncResult.Errors.Select(x => x.Message)));
            var totals = JsonSerializer.Deserialize<MercadoLivreSyncNowResult>(job.ResultJson) ?? new();
            totals.OrdersUpserted += syncResult.Data?.OrdersUpserted ?? 0;
            totals.ItemsUpserted += syncResult.Data?.ItemsUpserted ?? 0;
            totals.ReservationsCreated += syncResult.Data?.ReservationsCreated ?? 0;
            job.ResultJson = JsonSerializer.Serialize(totals);
            job.Checkpoint = segmentTo.ToString("O", CultureInfo.InvariantCulture);
            if (segmentTo >= job.RangeTo)
            {
                job.Status = "COMPLETED";
                job.Processed = 1;
                job.Total = 1;
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                // Persist a bounded six-hour segment at a time. A restart repeats at
                // most the unfinished segment, never the full 30-day chunk.
                job.Status = "PENDING";
                job.Attempts = 0;
                job.NextAttemptAt = null;
            }
            job.LastError = null;
            _logger.LogInformation("Financial sync segment completed job={JobId} seller={SellerId} checkpoint={Checkpoint} chunkComplete={ChunkComplete}",
                job.Id, job.SellerId, job.Checkpoint, job.Status == "COMPLETED");
            }
        }
        catch (BillingRateLimitedException ex)
        {
            job.Status = "RETRY";
            job.NextAttemptAt = DateTimeOffset.UtcNow.Add(ex.RetryAfter);
            job.LastError = ex.Message;
        }
        catch (BillingPartialContentException ex)
        {
            job.Status = "RETRY";
            job.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(15);
            job.LastError = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A failed sync may leave invalid order entities tracked by this same
            // DbContext (job itself was already detached above). Clearing the tracker
            // keeps those out of every save that follows, including UpdateParentAsync's.
            if (_db is DbContext context)
                context.ChangeTracker.Clear();
            job.Status = job.Attempts >= 8 ? "FAILED" : "RETRY";
            var exponent = Math.Min(job.Attempts, 5);
            var ceilingSeconds = Math.Min(900, 30 * (1 << exponent));
            job.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Random.Shared.Next(30, ceilingSeconds + 1));
            job.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            _logger.LogWarning("Financial sync chunk {Status} job={JobId} seller={SellerId} attempt={Attempt} errorType={ErrorType} nextAttempt={NextAttemptAt}",
                job.Status, job.Id, job.SellerId, job.Attempts, ex.GetType().Name, job.NextAttemptAt);
        }
        finally
        {
            // Only persist this attempt's outcome, and only release the lease, while
            // this worker still owns it (WHERE LockedBy = workerId). If another worker
            // already reclaimed the row because the lease expired mid-call, this write
            // is a deliberate no-op: overwriting that worker's newer progress here would
            // silently lose whatever it already accomplished, and freeing a lease that
            // is not ours to free could let a third worker pile onto the same row.
            var releasedAt = DateTimeOffset.UtcNow;
            int updatedRows;
            if (_db.Database.IsRelational())
            {
                updatedRows = await _db.FinancialSyncJobs
                    .Where(x => x.Id == job.Id && x.LockedBy == workerId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Status, job.Status)
                        .SetProperty(x => x.Checkpoint, job.Checkpoint)
                        .SetProperty(x => x.ResultJson, job.ResultJson)
                        .SetProperty(x => x.Processed, job.Processed)
                        .SetProperty(x => x.Total, job.Total)
                        .SetProperty(x => x.Attempts, job.Attempts)
                        .SetProperty(x => x.NextAttemptAt, job.NextAttemptAt)
                        .SetProperty(x => x.LastError, job.LastError)
                        .SetProperty(x => x.CompletedAt, job.CompletedAt)
                        .SetProperty(x => x.LockedBy, (string?)null)
                        .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
                        .SetProperty(x => x.UpdatedAt, releasedAt),
                        cancellationToken);
            }
            else
            {
                // ExecuteUpdateAsync is not supported by the InMemory provider used in
                // tests; fall back to an equivalent read-check-write guarded by the same
                // LockedBy comparison.
                var current = await _db.FinancialSyncJobs.SingleOrDefaultAsync(x => x.Id == job.Id, cancellationToken);
                if (current != null && current.LockedBy == workerId)
                {
                    current.Status = job.Status;
                    current.Checkpoint = job.Checkpoint;
                    current.ResultJson = job.ResultJson;
                    current.Processed = job.Processed;
                    current.Total = job.Total;
                    current.Attempts = job.Attempts;
                    current.NextAttemptAt = job.NextAttemptAt;
                    current.LastError = job.LastError;
                    current.CompletedAt = job.CompletedAt;
                    current.LockedBy = null;
                    current.LeaseUntil = null;
                    current.UpdatedAt = releasedAt;
                    await _db.SaveChangesAsync(cancellationToken);
                    updatedRows = 1;
                }
                else
                {
                    updatedRows = 0;
                }
            }

            if (updatedRows == 0)
            {
                _logger.LogWarning(
                    "Financial sync chunk job={JobId} seller={SellerId} lease was reclaimed by another worker before {WorkerId} finished; this attempt's result was discarded to avoid overwriting newer progress.",
                    job.Id, job.SellerId, workerId);
            }

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
