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
    private const string OperationalHistoryAlgorithm = "ml-history-hourly-v1";
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
                    // The same orders can legitimately be reconciled again in a later
                    // batch when Mercado Pago publishes a late adjustment. Keep the
                    // child idempotent inside this batch without colliding with a
                    // previous batch that contained the same set of orders.
                    DedupeKey = $"BILLING:ORDERS:{sellerGroup.Key}:{parent.Id:N}:{orderHash}",
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
        lookbackDays = Math.Clamp(lookbackDays, 1, 366);
        chunkDays = Math.Clamp(chunkDays, 1, 7);
        var isHistoricalBackfill = lookbackDays >= 365;
        var sellers = await _db.TenantMarketplaceConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientId == clientId && x.Provider == MarketplaceProvider.MercadoLivre)
            .Where(x => !sellerId.HasValue || x.SellerId == sellerId.Value)
            .Select(x => x.SellerId).Distinct().ToListAsync(cancellationToken);
        if (sellers.Count == 0) throw new InvalidOperationException("Nenhum seller Mercado Livre conectado.");

        var result = new FinancialSyncEnqueueResult();
        // ML applies hour precision to order search filters. Canonical, aligned
        // boundaries avoid losing orders at a minute/second boundary.
        var now = DateTimeOffset.UtcNow;
        // A canonical day boundary keeps OAuth retries and repeated manual clicks
        // on the same annual interval/idempotency key. The recent incremental sync
        // owns the still-open UTC day.
        var to = isHistoricalBackfill
            ? new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
        var from = isHistoricalBackfill ? to.AddMonths(-12) : to.AddDays(-lookbackDays);
        from = new DateTimeOffset(from.Year, from.Month, from.Day, from.Hour, 0, 0, TimeSpan.Zero);
        foreach (var seller in sellers)
        {
            var stableDedupe = isHistoricalBackfill
                ? $"OP:HISTORY:{OperationalHistoryAlgorithm}:{tenantId}:{clientId:N}:{seller}:{from:yyyyMMddHH}:{to:yyyyMMddHH}"
                : $"OP:RECENT:{OperationalHistoryAlgorithm}:{tenantId}:{clientId:N}:{seller}:{from:yyyyMMddHH}:{to:yyyyMMddHH}:{now:yyyyMMddHHmmssfffffff}";
            var batch = await _db.FinancialSyncJobs.FirstOrDefaultAsync(x =>
                x.TenantId == tenantId && x.ClientId == clientId && x.Provider == MarketplaceProvider.MercadoLivre
                && x.SellerId == seller && x.JobType == FinancialSyncJobTypes.OperationalSyncBatch
                && x.DedupeKey == stableDedupe, cancellationToken);
            var needsWork = false;
            if (batch == null && isHistoricalBackfill)
            {
                var active = await _db.FinancialSyncJobs.AsNoTracking().FirstOrDefaultAsync(x =>
                    x.TenantId == tenantId && x.ClientId == clientId && x.Provider == MarketplaceProvider.MercadoLivre
                    && x.SellerId == seller && x.JobType == FinancialSyncJobTypes.OperationalSyncBatch
                    && (x.Status == "INITIAL_PENDING" || x.Status == "BACKFILLING"), cancellationToken);
                if (active != null)
                {
                    result.Jobs.Add(Map(active));
                    continue;
                }
            }
            if (batch == null)
            {
                batch = new FinancialSyncJob
                {
                    TenantId = tenantId, ClientId = clientId, Provider = MarketplaceProvider.MercadoLivre, SellerId = seller,
                    JobType = FinancialSyncJobTypes.OperationalSyncBatch, RangeFrom = from, RangeTo = to,
                    DedupeKey = stableDedupe, Status = "INITIAL_PENDING",
                    PayloadJson = JsonSerializer.Serialize(new { algorithmVersion = OperationalHistoryAlgorithm, overlapHours = 1 })
                };
                _db.FinancialSyncJobs.Add(batch);
                needsWork = true;
            }
            else
            {
                // A durable historical coverage repairs missing windows instead of
                // starting a competing annual process. Recent manual syncs always
                // create a fresh batch so late shipment/status changes are observed.
                if (from < batch.RangeFrom) batch.RangeFrom = from;
                if (to > batch.RangeTo) batch.RangeTo = to;
            }
            var existingWindows = await _db.FinancialSyncJobs.AsNoTracking()
                .Where(x => x.ParentJobId == batch.Id && x.JobType == FinancialSyncJobTypes.OperationalSyncChunk)
                .Select(x => new { x.RangeFrom, x.RangeTo, x.Status }).ToListAsync(cancellationToken);
            for (var cursor = from; cursor < to; cursor = cursor.AddDays(chunkDays))
            {
                var chunkTo = cursor.AddDays(chunkDays) < to ? cursor.AddDays(chunkDays) : to;
                var existingWindow = existingWindows.FirstOrDefault(x => x.RangeFrom == cursor && x.RangeTo == chunkTo);
                if (existingWindow != null)
                {
                    if (existingWindow.Status is "FAILED" or "PARTIAL")
                    {
                        var tracked = await _db.FinancialSyncJobs.SingleAsync(x => x.ParentJobId == batch.Id
                            && x.JobType == FinancialSyncJobTypes.OperationalSyncChunk
                            && x.RangeFrom == cursor && x.RangeTo == chunkTo, cancellationToken);
                        tracked.Status = "PENDING";
                        tracked.NextAttemptAt = null;
                        tracked.LastError = null;
                        needsWork = true;
                    }
                    continue;
                }
                _db.FinancialSyncJobs.Add(new FinancialSyncJob
                {
                    ParentJobId = batch.Id, TenantId = tenantId, ClientId = clientId, Provider = MarketplaceProvider.MercadoLivre,
                    SellerId = seller, JobType = FinancialSyncJobTypes.OperationalSyncChunk, RangeFrom = cursor, RangeTo = chunkTo,
                    DedupeKey = $"OP:CHUNK:{OperationalHistoryAlgorithm}:{tenantId}:{clientId:N}:{seller}:{cursor:O}:{chunkTo:O}", Status = "PENDING",
                    Checkpoint = JsonSerializer.Serialize(new HistoryPageCheckpoint(cursor, 0))
                });
                needsWork = true;
            }
            if (needsWork)
            {
                batch.Status = "BACKFILLING";
                batch.CompletedAt = null;
            }
            batch.UpdatedAt = DateTimeOffset.UtcNow;
            result.Jobs.Add(Map(batch));
        }
        await _db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<int> EnsureExistingSellerHistoryAsync(CancellationToken cancellationToken = default)
    {
        var scopes = await _db.TenantMarketplaceConnections.AsNoTracking()
            .Where(x => x.Provider == MarketplaceProvider.MercadoLivre && x.SellerId > 0)
            .Select(x => new { x.TenantId, x.ClientId, x.SellerId }).Distinct()
            .ToListAsync(cancellationToken);
        foreach (var scope in scopes)
            await EnqueueOperationalBackfillAsync(scope.TenantId, scope.ClientId, scope.SellerId,
                lookbackDays: 366, chunkDays: 1, cancellationToken: cancellationToken);
        return scopes.Count;
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

    public async Task<MercadoLivreHistorySyncStatusResult> GetHistoryStatusAsync(
        string tenantId, Guid clientId, CancellationToken cancellationToken = default)
    {
        var parents = await _db.FinancialSyncJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientId == clientId
                && x.Provider == MarketplaceProvider.MercadoLivre
                && x.JobType == FinancialSyncJobTypes.OperationalSyncBatch
                && x.DedupeKey.StartsWith("OP:HISTORY:"))
            .OrderByDescending(x => x.RangeTo).ThenByDescending(x => x.CreatedAt).ToListAsync(cancellationToken);
        parents = parents.GroupBy(x => x.SellerId).Select(x => x.First()).OrderBy(x => x.SellerId).ToList();
        var nicknames = await _db.TenantMarketplaceConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientId == clientId
                && x.Provider == MarketplaceProvider.MercadoLivre)
            .ToDictionaryAsync(x => x.SellerId, x => x.Nickname, cancellationToken);
        var result = new MercadoLivreHistorySyncStatusResult();
        foreach (var parent in parents)
        {
            var children = await _db.FinancialSyncJobs.AsNoTracking()
                .Where(x => x.ParentJobId == parent.Id
                    && (x.JobType == FinancialSyncJobTypes.OperationalSyncChunk
                        || x.JobType == FinancialSyncJobTypes.OperationalSyncGapRetry))
                .OrderBy(x => x.UpdatedAt).ToListAsync(cancellationToken);
            var aggregate = AggregateHistory(children);
            var discovered = aggregate.Orders.Keys.ToArray();
            var imported = discovered.Length == 0 ? 0 : await _db.MarketplaceOrders.AsNoTracking()
                .CountAsync(x => x.TenantId == tenantId && x.ClientId == clientId
                    && x.Provider == MarketplaceProvider.MercadoLivre && x.SellerId == parent.SellerId
                    && discovered.Contains(x.MlOrderId), cancellationToken);
            var dateQuery = _db.MarketplaceOrders.AsNoTracking().Where(x => x.TenantId == tenantId
                && x.ClientId == clientId && x.Provider == MarketplaceProvider.MercadoLivre
                && x.SellerId == parent.SellerId
                && (x.ChannelCreatedAt ?? x.ImportedAt) >= parent.RangeFrom
                && (x.ChannelCreatedAt ?? x.ImportedAt) < parent.RangeTo);
            var oldest = await dateQuery.MinAsync(x => (DateTimeOffset?)(x.ChannelCreatedAt ?? x.ImportedAt), cancellationToken);
            var newest = await dateQuery.MaxAsync(x => (DateTimeOffset?)(x.ChannelCreatedAt ?? x.ImportedAt), cancellationToken);
            result.Sellers.Add(new MercadoLivreHistorySellerStatusResult
            {
                JobId = parent.Id,
                SellerId = parent.SellerId,
                Nickname = nicknames.GetValueOrDefault(parent.SellerId),
                Status = parent.Status,
                CoverageFrom = parent.RangeFrom,
                CoverageTo = parent.RangeTo,
                // This is deliberately a diagnostic (largest provider-reported
                // window), never a sum of overlapping window paging totals.
                RemoteReportedTotal = aggregate.RemoteReportedByWindow.Values.DefaultIfEmpty(0).Max(),
                DiscoveredUniqueOrderIds = aggregate.Orders.Count,
                LocalImportedOrderIds = imported,
                ResolvedUnavailableOrderIds = aggregate.Orders.Count(x => x.Value == HistoryOrderStates.Unavailable),
                UnresolvedGapOrderIds = aggregate.Orders.Count(x => x.Value == HistoryOrderStates.Gap),
                CompletedWindows = children.Count(x => x.JobType == FinancialSyncJobTypes.OperationalSyncChunk
                    && x.Status is "COMPLETED" or "PARTIAL"),
                TotalWindows = children.Count(x => x.JobType == FinancialSyncJobTypes.OperationalSyncChunk),
                OldestOrderAt = oldest,
                NewestOrderAt = newest,
                LastError = children.LastOrDefault(x => !string.IsNullOrWhiteSpace(x.LastError))?.LastError
            });
        }
        result.OverallStatus = result.Sellers.Count == 0 ? "INITIAL_PENDING"
            : result.Sellers.Any(x => x.Status == "FAILED") ? "FAILED"
            : result.Sellers.Any(x => x.Status == "PARTIAL_WITH_GAPS") ? "PARTIAL_WITH_GAPS"
            : result.Sellers.All(x => x.Status == "CURRENT") ? "CURRENT"
            : "BACKFILLING";
        return result;
    }

    public async Task<FinancialSyncJobResult> RetryHistoryGapsAsync(
        string tenantId, Guid jobId, CancellationToken cancellationToken = default)
    {
        var parent = await _db.FinancialSyncJobs.FirstOrDefaultAsync(x => x.Id == jobId
            && x.TenantId == tenantId && x.JobType == FinancialSyncJobTypes.OperationalSyncBatch,
            cancellationToken) ?? throw new InvalidOperationException("History sync job not found.");
        var children = await _db.FinancialSyncJobs.AsNoTracking()
            .Where(x => x.ParentJobId == parent.Id).OrderBy(x => x.UpdatedAt).ToListAsync(cancellationToken);
        var gaps = AggregateHistory(children).Orders
            .Where(x => x.Value == HistoryOrderStates.Gap).Select(x => x.Key)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (gaps.Length == 0) throw new InvalidOperationException("History sync has no unresolved gaps.");
        var payload = JsonSerializer.Serialize(gaps);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var active = await _db.FinancialSyncJobs.AsNoTracking().FirstOrDefaultAsync(x => x.ParentJobId == parent.Id
            && x.JobType == FinancialSyncJobTypes.OperationalSyncGapRetry && x.PayloadJson == payload
            && (x.Status == "PENDING" || x.Status == "RUNNING" || x.Status == "RETRY"), cancellationToken);
        if (active != null) return Map(active);
        var retryNumber = children.Count(x => x.JobType == FinancialSyncJobTypes.OperationalSyncGapRetry) + 1;
        var retry = new FinancialSyncJob
        {
            ParentJobId = parent.Id, TenantId = parent.TenantId, ClientId = parent.ClientId,
            Provider = parent.Provider, SellerId = parent.SellerId,
            JobType = FinancialSyncJobTypes.OperationalSyncGapRetry,
            RangeFrom = parent.RangeFrom, RangeTo = parent.RangeTo, Status = "PENDING",
            DedupeKey = $"OP:GAPS:{OperationalHistoryAlgorithm}:{parent.Id:N}:{hash}:{retryNumber}",
            PayloadJson = payload
        };
        _db.FinancialSyncJobs.Add(retry);
        parent.Status = "BACKFILLING";
        parent.CompletedAt = null;
        parent.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return Map(retry);
    }

    public async Task<bool> ProcessNextAsync(string workerId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        FinancialSyncJob? job;
        await using (var transaction = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(cancellationToken) : null)
        {
            var query = _db.FinancialSyncJobs.Where(x => (x.JobType == FinancialSyncJobTypes.OperationalSyncChunk
                    || x.JobType == FinancialSyncJobTypes.OperationalSyncGapRetry
                    || x.JobType == FinancialSyncJobTypes.BillingReconciliation)
                && (x.Status == "PENDING" || x.Status == "RETRY" || x.Status == "RUNNING")
                && (!x.NextAttemptAt.HasValue || x.NextAttemptAt <= now)
                && (!x.LeaseUntil.HasValue || x.LeaseUntil < now))
                .OrderBy(x => x.JobType == FinancialSyncJobTypes.OperationalSyncChunk ? 0 : 1)
                .ThenBy(x => x.UpdatedAt)
                .ThenBy(x => x.SellerId)
                .ThenBy(x => x.CreatedAt);
            job = _db.Database.IsRelational()
                ? await _db.FinancialSyncJobs.FromSqlRaw("SELECT * FROM financial_sync_jobs WHERE job_type IN ('OPERATIONAL_SYNC_CHUNK','OPERATIONAL_SYNC_GAP_RETRY','BILLING_RECONCILIATION') AND status IN ('PENDING','RETRY','RUNNING') AND (next_attempt_at IS NULL OR next_attempt_at <= now()) AND (lease_until IS NULL OR lease_until < now()) ORDER BY CASE WHEN job_type IN ('OPERATIONAL_SYNC_CHUNK','OPERATIONAL_SYNC_GAP_RETRY') THEN 0 ELSE 1 END, updated_at, seller_id, created_at FOR UPDATE SKIP LOCKED LIMIT 1").FirstOrDefaultAsync(cancellationToken)
                : await query.FirstOrDefaultAsync(cancellationToken);
            if (job == null) return false;
            job.Status = "RUNNING";
            job.LockedBy = workerId;
            // Each claim processes one persisted page of the canonical window.
            // The window boundaries are aligned to the hour because ML truncates
            // filter precision; pagination is the recovery unit.
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

        var pageCheckpoint = ParseCheckpoint(job.Checkpoint, job.RangeFrom);
        var segmentFrom = job.RangeFrom;
        var segmentTo = job.RangeTo;
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
            else if (job.JobType == FinancialSyncJobTypes.OperationalSyncGapRetry)
            {
                var ids = JsonSerializer.Deserialize<string[]>(job.PayloadJson) ?? [];
                var syncResult = await _sync.SyncDiscoveredOrdersAsync(
                    job.TenantId, job.ClientId, job.SellerId, ids, cancellationToken);
                if (!syncResult.Succeeded)
                    throw new InvalidOperationException(string.Join("; ", syncResult.Errors.Select(x => x.Message)));
                var state = new HistoryChunkResult();
                MergeOrderStates(state, ids, syncResult.Data);
                job.ResultJson = JsonSerializer.Serialize(state);
                job.Status = state.Orders.Values.Any(x => x == HistoryOrderStates.Gap) ? "PARTIAL" : "COMPLETED";
                job.Processed = 1;
                job.Total = 1;
                job.CompletedAt = DateTimeOffset.UtcNow;
                job.LastError = null;
            }
            else
            {
                // Adjacent canonical windows overlap by one hour. Mercado Livre
                // discards minute/second precision in these filters, so overlap
                // plus (sellerId, orderId) deduplication avoids boundary loss.
                var queryFrom = segmentFrom.AddHours(-1);
                var pageResult = await _sync.SearchOrderPageAsync(job.TenantId, job.ClientId, job.SellerId,
                    queryFrom, segmentTo, pageCheckpoint.Offset, 50, cancellationToken);
                if (!pageResult.Succeeded || pageResult.Data == null)
                    throw new InvalidOperationException(string.Join("; ", pageResult.Errors.Select(x => x.Message)));
                var page = pageResult.Data;
                var syncResult = await _sync.SyncDiscoveredOrdersAsync(job.TenantId, job.ClientId, job.SellerId,
                    page.OrderIds, cancellationToken);
                if (!syncResult.Succeeded)
                    throw new InvalidOperationException(string.Join("; ", syncResult.Errors.Select(x => x.Message)));

                var state = JsonSerializer.Deserialize<HistoryChunkResult>(job.ResultJson) ?? new();
                state.RemoteReportedByWindow[segmentFrom.ToString("O", CultureInfo.InvariantCulture)] = page.RemoteReportedTotal;
                MergeOrderStates(state, page.OrderIds, syncResult.Data);
                job.ResultJson = JsonSerializer.Serialize(state);

                if (page.HasMore)
                {
                    job.Checkpoint = JsonSerializer.Serialize(new HistoryPageCheckpoint(segmentFrom,
                        page.Offset + page.OrderIds.Count));
                    job.Status = "PENDING";
                }
                else
                {
                    job.Checkpoint = JsonSerializer.Serialize(new HistoryPageCheckpoint(segmentFrom, 0));
                    job.Status = state.Orders.Values.Any(x => x == HistoryOrderStates.Gap) ? "PARTIAL" : "COMPLETED";
                    job.Processed = 1;
                    job.Total = 1;
                    job.CompletedAt = DateTimeOffset.UtcNow;
                }
                job.Attempts = 0;
                job.NextAttemptAt = null;
                job.LastError = null;
                _logger.LogInformation("History page completed job={JobId} seller={SellerId} windowFrom={WindowFrom} offset={Offset} chunkStatus={Status}",
                    job.Id, job.SellerId, segmentFrom, page.Offset, job.Status);
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
        var windows = children.Where(x => x.JobType == FinancialSyncJobTypes.OperationalSyncChunk).ToList();
        if (parent.JobType == FinancialSyncJobTypes.OperationalSyncBatch)
        {
            parent.Processed = windows.Count(x => x.Status is "COMPLETED" or "PARTIAL");
            parent.Total = windows.Count;
            var aggregate = AggregateHistory(children.OrderBy(x => x.UpdatedAt));
            var unresolved = aggregate.Orders.Count(x => x.Value == HistoryOrderStates.Gap);
            parent.Status = windows.Any(x => x.Status == "FAILED") ? "FAILED"
                : windows.All(x => x.Status is "COMPLETED" or "PARTIAL") && unresolved == 0 ? "CURRENT"
                : windows.All(x => x.Status is "COMPLETED" or "PARTIAL") ? "PARTIAL_WITH_GAPS"
                : "BACKFILLING";
        }
        else
        {
            parent.Processed = children.Count(x => x.Status is "COMPLETED" or "PARTIAL");
            parent.Total = children.Count;
            parent.Status = children.Any(x => x.Status == "FAILED") ? "FAILED"
                : children.All(x => x.Status == "COMPLETED") ? "COMPLETED"
                : children.All(x => x.Status is "COMPLETED" or "PARTIAL") ? "PARTIAL" : "RUNNING";
        }
        parent.LastError = children.FirstOrDefault(x => x.Status == "FAILED")?.LastError;
        parent.CompletedAt = parent.Status is "CURRENT" or "PARTIAL_WITH_GAPS" or "COMPLETED" or "PARTIAL" or "FAILED"
            ? DateTimeOffset.UtcNow : null;
        parent.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private static FinancialSyncJobResult Map(FinancialSyncJob x) => new()
    {
        JobId = x.Id, SellerId = x.SellerId, JobType = x.JobType, Status = x.Status,
        RangeFrom = x.RangeFrom, RangeTo = x.RangeTo, Total = x.Total, Processed = x.Processed,
        LastError = x.LastError, CreatedAt = x.CreatedAt, CompletedAt = x.CompletedAt
    };

    private static HistoryPageCheckpoint ParseCheckpoint(string? json, DateTimeOffset fallback)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var value = JsonSerializer.Deserialize<HistoryPageCheckpoint>(json);
                if (value != null) return value;
            }
            catch (JsonException)
            {
                if (DateTimeOffset.TryParse(json, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var legacy))
                    return new HistoryPageCheckpoint(legacy, 0);
            }
        }
        return new HistoryPageCheckpoint(fallback, 0);
    }

    private static void MergeOrderStates(
        HistoryChunkResult state, IReadOnlyCollection<string> discovered, MercadoLivreSyncNowResult? sync)
    {
        var gaps = (sync?.UnresolvedGaps ?? []).Select(x => x.OrderId).ToHashSet(StringComparer.Ordinal);
        var unavailable = (sync?.ResolvedUnavailableOrderIds ?? []).ToHashSet(StringComparer.Ordinal);
        var imported = (sync?.ImportedOrderIds ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var id in discovered.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var next = imported.Contains(id) ? HistoryOrderStates.Imported
                : unavailable.Contains(id) ? HistoryOrderStates.Unavailable
                : gaps.Contains(id) ? HistoryOrderStates.Gap
                : HistoryOrderStates.Gap;
            if (!state.Orders.TryGetValue(id, out var current)
                || current == HistoryOrderStates.Gap
                || next == HistoryOrderStates.Imported)
                state.Orders[id] = next;
        }
    }

    private static HistoryChunkResult AggregateHistory(IEnumerable<FinancialSyncJob> children)
    {
        var aggregate = new HistoryChunkResult();
        foreach (var child in children)
        {
            HistoryChunkResult? part;
            try { part = JsonSerializer.Deserialize<HistoryChunkResult>(child.ResultJson); }
            catch (JsonException) { continue; }
            if (part == null) continue;
            foreach (var window in part.RemoteReportedByWindow)
                aggregate.RemoteReportedByWindow[window.Key] = window.Value;
            foreach (var order in part.Orders)
                aggregate.Orders[order.Key] = order.Value;
        }
        return aggregate;
    }

    private sealed record HistoryPageCheckpoint(DateTimeOffset SegmentFrom, int Offset);

    private sealed class HistoryChunkResult
    {
        public Dictionary<string, long> RemoteReportedByWindow { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Orders { get; set; } = new(StringComparer.Ordinal);
    }

    private static class HistoryOrderStates
    {
        public const string Imported = "IMPORTED";
        public const string Unavailable = "UNAVAILABLE";
        public const string Gap = "GAP";
    }
}
