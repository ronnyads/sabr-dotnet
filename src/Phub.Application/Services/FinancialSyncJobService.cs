using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class FinancialSyncJobService
{
    // Mercado Livre allows up to 1,000 results in /orders/search. We deliberately
    // consume 50 at a time because every id fans out into order/shipment/financial
    // detail calls; the fetch gate controls concurrency and protects the provider
    // quota while this larger page removes the default 10-result bottleneck.
    private const int OperationalHistoryPageSize = 50;
    private const string OperationalHistoryAlgorithm = "ml-history-hourly-v1";
    private const string SkippedBeforeFirstOrder = "SKIPPED_BEFORE_FIRST_ORDER";
    private const string SkippedOutsideRollingWindow = "SKIPPED_OUTSIDE_ROLLING_WINDOW";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> EnqueueGates = new(StringComparer.Ordinal);
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
        CancellationToken cancellationToken = default, bool discoverBoundaries = true)
    {
        lookbackDays = Math.Clamp(lookbackDays, 1, 366);
        chunkDays = Math.Clamp(chunkDays, 1, 7);
        var isHistoricalBackfill = lookbackDays >= 365;
        var sellers = await _db.TenantMarketplaceConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientId == clientId && x.Provider == MarketplaceProvider.MercadoLivre)
            .Where(x => !sellerId.HasValue || x.SellerId == sellerId.Value)
            .Select(x => x.SellerId).Distinct().OrderBy(x => x).ToListAsync(cancellationToken);
        if (sellers.Count == 0) throw new InvalidOperationException("Nenhum seller Mercado Livre conectado.");

        // Keep simultaneous clicks/callbacks idempotent in this process; PostgreSQL's
        // transaction-scoped advisory lock below coordinates separate app instances.
        using var enqueueGateLease = await AcquireEnqueueGatesAsync(
            tenantId, clientId, sellers, cancellationToken);
        var result = new FinancialSyncEnqueueResult();
        // ML applies hour precision to order search filters. Canonical, aligned
        // boundaries avoid losing orders at a minute/second boundary.
        var now = DateTimeOffset.UtcNow;
        // A canonical day boundary keeps OAuth retries and repeated manual clicks
        // on the same annual interval/idempotency key. The recent incremental sync
        // owns the still-open UTC day.
        // Both lanes use a stable UTC day boundary. The previous recent lane moved
        // its interval every hour, so every repair created another overlapping set.
        // Webhooks and the incremental synchronizer own the still-open UTC day.
        var to = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        var from = isHistoricalBackfill ? to.AddMonths(-12) : to.AddDays(-lookbackDays);
        from = new DateTimeOffset(from.Year, from.Month, from.Day, from.Hour, 0, 0, TimeSpan.Zero);

        // Provider discovery is network I/O. Resolve it before opening the enqueue
        // transaction/advisory locks so one slow seller never holds PostgreSQL locks
        // while Mercado Livre responds.
        var discoveredPlans = new Dictionary<long, HistoryBoundaryPlan?>();
        if (isHistoricalBackfill && discoverBoundaries)
        {
            foreach (var seller in sellers)
            {
                var existingPayload = await _db.FinancialSyncJobs.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.ClientId == clientId
                        && x.Provider == MarketplaceProvider.MercadoLivre && x.SellerId == seller
                        && x.JobType == FinancialSyncJobTypes.OperationalSyncBatch
                        && x.DedupeKey.StartsWith($"OP:HISTORY:{OperationalHistoryAlgorithm}:"))
                    .OrderByDescending(x => x.UpdatedAt).Select(x => x.PayloadJson)
                    .FirstOrDefaultAsync(cancellationToken);
                var cached = ParseHistoryBoundaryPlan(existingPayload);
                if (cached?.DiscoveryCompletedAt != null
                    && cached.DiscoveryFrom == from && cached.DiscoveryTo == to)
                {
                    discoveredPlans[seller] = cached;
                    continue;
                }

                var discovery = await _sync.DiscoverHistoryBoundaryAsync(
                    tenantId, clientId, seller, from, to, cancellationToken);
                if (discovery.Succeeded && discovery.Data?.IsConclusive == true)
                {
                    var firstOrderAt = discovery.Data.FirstOrderAt;
                    var discoveredDay = firstOrderAt.HasValue ? StartOfUtcDay(firstOrderAt.Value) : to;
                    discoveredPlans[seller] = new HistoryBoundaryPlan
                    {
                        AlgorithmVersion = OperationalHistoryAlgorithm,
                        OverlapHours = 1,
                        DiscoveryFrom = from,
                        DiscoveryTo = to,
                        FirstOrderAt = firstOrderAt,
                        WorkFrom = discoveredDay < from ? from : discoveredDay,
                        DiscoveryCompletedAt = DateTimeOffset.UtcNow,
                        RemoteReportedTotal = discovery.Data.RemoteReportedTotal
                    };
                }
                else
                {
                    // An inconclusive probe must never hide history. The full range
                    // remains the safe fallback and discovery is retried next repair.
                    discoveredPlans[seller] = null;
                }
            }
        }

        await using var enqueueTransaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        foreach (var seller in sellers)
        {
            // One durable parent per seller and algorithm. The covered interval is
            // mutable metadata; putting it in the key used to create a new annual
            // process whenever the clock crossed a boundary.
            var jobPrefix = isHistoricalBackfill ? "OP:HISTORY" : "OP:RECENT";
            var stableDedupe = $"{jobPrefix}:{OperationalHistoryAlgorithm}:{tenantId}:{clientId:N}:{seller}";
            if (_db.Database.IsRelational())
            {
                var lockKey = $"ML_SYNC_QUEUE:{tenantId}:{clientId:N}:{seller}:{jobPrefix}";
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtext({lockKey}));", cancellationToken);
            }
            var batch = await _db.FinancialSyncJobs.FirstOrDefaultAsync(x =>
                x.TenantId == tenantId && x.ClientId == clientId && x.Provider == MarketplaceProvider.MercadoLivre
                && x.SellerId == seller && x.JobType == FinancialSyncJobTypes.OperationalSyncBatch
                && x.DedupeKey == stableDedupe, cancellationToken);
            var needsWork = false;

            // Adopt the most recent parent created by the interval-key algorithm.
            // This upgrades production in place and preserves its checkpoints.
            if (batch == null)
            {
                batch = await _db.FinancialSyncJobs
                    .Where(x =>
                    x.TenantId == tenantId && x.ClientId == clientId && x.Provider == MarketplaceProvider.MercadoLivre
                    && x.SellerId == seller && x.JobType == FinancialSyncJobTypes.OperationalSyncBatch
                    && x.DedupeKey.StartsWith($"{jobPrefix}:{OperationalHistoryAlgorithm}:"))
                    .OrderByDescending(x => x.UpdatedAt)
                    .ThenByDescending(x => x.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);
                if (batch != null) batch.DedupeKey = stableDedupe;
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
                // starting a competing annual process. Recent refreshes reopen the
                // durable rolling windows so late shipment/status changes are seen.
                batch.PayloadJson = JsonSerializer.Serialize(new
                    { algorithmVersion = OperationalHistoryAlgorithm, overlapHours = 1 });
                if (from < batch.RangeFrom) batch.RangeFrom = from;
                if (to > batch.RangeTo) batch.RangeTo = to;
            }


            var workFrom = from;
            HistoryBoundaryPlan? historyPlan = null;
            if (isHistoricalBackfill)
            {
                historyPlan = discoveredPlans.GetValueOrDefault(seller);
                workFrom = historyPlan == null ? from
                    : historyPlan.WorkFrom < from ? from : historyPlan.WorkFrom;
            }

            batch.PayloadJson = historyPlan == null
                ? JsonSerializer.Serialize(new { algorithmVersion = OperationalHistoryAlgorithm, overlapHours = 1 })
                : JsonSerializer.Serialize(historyPlan);
            var existingWindows = await _db.FinancialSyncJobs
                .Where(x => x.ParentJobId == batch.Id && x.JobType == FinancialSyncJobTypes.OperationalSyncChunk)
                .ToListAsync(cancellationToken);
            if (isHistoricalBackfill && workFrom > from)
            {
                var obsoleteIds = await _db.FinancialSyncJobs
                    .Where(x => x.ParentJobId == batch.Id
                        && x.JobType == FinancialSyncJobTypes.OperationalSyncChunk
                        && x.RangeTo <= workFrom
                        && (x.Status != "RUNNING" || !x.LeaseUntil.HasValue || x.LeaseUntil < now)
                        && x.Status != SkippedBeforeFirstOrder)
                    .Select(x => x.Id).ToListAsync(cancellationToken);
                if (obsoleteIds.Count > 0)
                {
                    var obsolete = await _db.FinancialSyncJobs
                        .Where(x => obsoleteIds.Contains(x.Id)).ToListAsync(cancellationToken);
                    foreach (var window in obsolete)
                    {
                        window.Status = SkippedBeforeFirstOrder;
                        window.NextAttemptAt = null;
                        window.LastError = null;
                        window.CompletedAt = DateTimeOffset.UtcNow;
                    }
                }
            }
            if (!isHistoricalBackfill)
            {
                var desiredRecentWindows = new HashSet<(DateTimeOffset From, DateTimeOffset To)>();
                for (var cursor = workFrom; cursor < to; cursor = cursor.AddDays(chunkDays))
                {
                    var chunkTo = cursor.AddDays(chunkDays) < to ? cursor.AddDays(chunkDays) : to;
                    desiredRecentWindows.Add((cursor, chunkTo));
                }
                foreach (var window in existingWindows.Where(x =>
                             !desiredRecentWindows.Contains((x.RangeFrom, x.RangeTo))
                             && x.Status != SkippedOutsideRollingWindow))
                {
                    // Preserve old hourly-shifted rows for audit, but revoke them
                    // from the live queue. An in-flight stale attempt cannot publish
                    // afterwards because its LockedBy compare-and-set no longer owns
                    // this row.
                    window.Status = SkippedOutsideRollingWindow;
                    window.LockedBy = null;
                    window.LeaseUntil = null;
                    window.NextAttemptAt = null;
                    window.LastError = "Superseded by canonical UTC-day rolling window.";
                    window.CompletedAt = DateTimeOffset.UtcNow;
                    window.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
            for (var cursor = workFrom; cursor < to; cursor = cursor.AddDays(chunkDays))
            {
                var chunkTo = cursor.AddDays(chunkDays) < to ? cursor.AddDays(chunkDays) : to;
                var existingWindow = existingWindows.FirstOrDefault(x => x.RangeFrom == cursor && x.RangeTo == chunkTo);
                if (existingWindow != null)
                {
                    // Historical windows are immutable once reconciled. A manual
                    // recent refresh reuses the same durable window instead of
                    // accumulating another job row on every click.
                    var isLatestClosedRecentWindow = !isHistoricalBackfill && chunkTo == to;
                    if (existingWindow.Status is "FAILED" or "PARTIAL"
                        || (isLatestClosedRecentWindow && existingWindow.Status == "COMPLETED"))
                    {
                        existingWindow.Status = "PENDING";
                        existingWindow.NextAttemptAt = null;
                        existingWindow.LastError = null;
                        needsWork = true;
                    }
                    continue;
                }
                _db.FinancialSyncJobs.Add(new FinancialSyncJob
                {
                    ParentJobId = batch.Id, TenantId = tenantId, ClientId = clientId, Provider = MarketplaceProvider.MercadoLivre,
                    SellerId = seller, JobType = FinancialSyncJobTypes.OperationalSyncChunk, RangeFrom = cursor, RangeTo = chunkTo,
                    // History and recent refreshes overlap intentionally, but each
                    // bounded lane must reuse only its own durable window row.
                    DedupeKey = $"OP:CHUNK:{jobPrefix}:{OperationalHistoryAlgorithm}:{tenantId}:{clientId:N}:{seller}:{cursor:O}:{chunkTo:O}", Status = "PENDING",
                    Checkpoint = JsonSerializer.Serialize(new HistoryPageCheckpoint(cursor, 0))
                });
                needsWork = true;
            }
            if (needsWork)
            {
                batch.Status = "BACKFILLING";
                batch.CompletedAt = null;
            }
            if (isHistoricalBackfill && workFrom >= to)
            {
                batch.Status = "CURRENT";
                batch.Processed = 0;
                batch.Total = 0;
                batch.CompletedAt = DateTimeOffset.UtcNow;
            }
            batch.UpdatedAt = DateTimeOffset.UtcNow;
            result.Jobs.Add(Map(batch));
        }
        await _db.SaveChangesAsync(cancellationToken);
        if (enqueueTransaction != null)
            await enqueueTransaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<FinancialSyncEnqueueResult> EnqueueCompleteOperationalSyncAsync(
        string tenantId, Guid clientId, long? sellerId,
        CancellationToken cancellationToken = default, bool discoverBoundaries = true)
    {
        // One user command guarantees the durable 12-month coverage and also
        // refreshes the recent/open window. Both paths are idempotent.
        var history = await EnqueueOperationalBackfillAsync(
            tenantId, clientId, sellerId, lookbackDays: 366, chunkDays: 1,
            cancellationToken: cancellationToken, discoverBoundaries: discoverBoundaries);
        var recent = await EnqueueOperationalBackfillAsync(
            tenantId, clientId, sellerId, lookbackDays: 30, chunkDays: 1,
            cancellationToken: cancellationToken);
        foreach (var job in recent.Jobs)
        {
            if (history.Jobs.All(x => x.JobId != job.JobId)) history.Jobs.Add(job);
        }
        return history;
    }

    public async Task<int> EnsureExistingSellerHistoryAsync(CancellationToken cancellationToken = default)
    {
        var scopes = await _db.TenantMarketplaceConnections.AsNoTracking()
            .Where(x => x.Provider == MarketplaceProvider.MercadoLivre && x.SellerId > 0)
            .Select(x => new { x.TenantId, x.ClientId, x.SellerId }).Distinct()
            .ToListAsync(cancellationToken);
        var ensured = 0;
        foreach (var scope in scopes)
        {
            try
            {
                // Repair both durable history and the canonical recent lane. This
                // also retires legacy hour-shifted recent windows automatically,
                // without requiring the seller to press the manual sync button.
                await EnqueueCompleteOperationalSyncAsync(
                    scope.TenantId, scope.ClientId, scope.SellerId, cancellationToken);
                ensured++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Isolation is per tenant/client/seller: one expired token or slow
                // provider response must not prevent the remaining stores from being
                // repaired in the same cycle.
                _logger.LogWarning(ex,
                    "Mercado Livre history ensure failed tenant={TenantId} client={ClientId} seller={SellerId}; continuing other sellers",
                    scope.TenantId, scope.ClientId, scope.SellerId);
                if (_db is DbContext context) context.ChangeTracker.Clear();
            }
        }
        return ensured;
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
            var statusNow = DateTimeOffset.UtcNow;
            var active = children
                .Where(x => x.Status == "RUNNING" && x.LeaseUntil >= statusNow)
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefault();
            var waiting = children
                .Where(x => x.Status is "PENDING" or "RETRY"
                    && (!x.NextAttemptAt.HasValue || x.NextAttemptAt <= statusNow))
                .OrderByDescending(x => x.RangeTo)
                .ThenBy(x => x.UpdatedAt)
                .FirstOrDefault();
            var current = active ?? waiting;
            var currentCheckpoint = current == null ? null : ParseCheckpoint(current.Checkpoint, current.RangeFrom);
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
                TotalWindows = children.Count(x => x.JobType == FinancialSyncJobTypes.OperationalSyncChunk
                    && !IsSkippedWindow(x.Status)),
                ActiveWindows = children.Count(x => x.Status == "RUNNING" && x.LeaseUntil >= statusNow),
                QueuedWindows = children.Count(x => x.Status is "PENDING" or "RETRY"),
                IsProcessing = active != null,
                CurrentWindowFrom = current?.RangeFrom,
                CurrentWindowTo = current?.RangeTo,
                CurrentPageOffset = currentCheckpoint?.Offset,
                LastActivityAt = children.Count == 0 ? parent.UpdatedAt : children.Max(x => x.UpdatedAt),
                NextAttemptAt = children.Where(x => x.Status == "RETRY" && x.NextAttemptAt.HasValue)
                    .Select(x => x.NextAttemptAt).OrderBy(x => x).FirstOrDefault(),
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
            if (transaction != null)
            {
                // Claiming is a very short critical section. Serializing only this
                // section prevents two worker lanes from concurrently claiming two
                // windows for the same tenant before either RUNNING lease is visible.
                // External Mercado Livre calls still execute concurrently afterwards.
                await _db.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_xact_lock(hashtext('FINANCIAL_SYNC_CLAIM_V2'))",
                    cancellationToken);
            }
            var query = _db.FinancialSyncJobs.Where(x => (x.JobType == FinancialSyncJobTypes.OperationalSyncChunk
                    || x.JobType == FinancialSyncJobTypes.OperationalSyncGapRetry
                    || x.JobType == FinancialSyncJobTypes.BillingReconciliation)
                && (x.Status == "PENDING" || x.Status == "RETRY" || x.Status == "RUNNING")
                && (!x.NextAttemptAt.HasValue || x.NextAttemptAt <= now)
                && (!x.LeaseUntil.HasValue || x.LeaseUntil < now)
                && !_db.FinancialSyncJobs.Any(active => active.Id != x.Id
                    && active.TenantId == x.TenantId
                    && active.Status == "RUNNING"
                    && active.LeaseUntil >= now)
                // The rolling lane overlaps the historical lane by design, but it
                // must not consume the same provider pages while initial history is
                // still open. Incremental webhooks keep new orders current meanwhile.
                && !(x.DedupeKey.StartsWith("OP:CHUNK:OP:RECENT:")
                    && _db.FinancialSyncJobs.Any(history =>
                        history.TenantId == x.TenantId
                        && history.ClientId == x.ClientId
                        && history.SellerId == x.SellerId
                        && history.DedupeKey.StartsWith("OP:CHUNK:OP:HISTORY:")
                        && (history.Status == "PENDING" || history.Status == "RETRY" || history.Status == "RUNNING"))))
                .OrderBy(x => x.JobType == FinancialSyncJobTypes.OperationalSyncChunk ? 0 : 1)
                // Process the newest missing day across every seller first. If one
                // seller owns hundreds of older windows it can no longer starve a
                // newly connected store whose current-period totals are incomplete.
                .ThenByDescending(x => x.RangeTo)
                .ThenBy(x => x.UpdatedAt)
                .ThenBy(x => x.SellerId)
                .ThenBy(x => x.CreatedAt);
            job = _db.Database.IsRelational()
                ? await _db.FinancialSyncJobs.FromSqlRaw("SELECT candidate.* FROM financial_sync_jobs candidate WHERE candidate.job_type IN ('OPERATIONAL_SYNC_CHUNK','OPERATIONAL_SYNC_GAP_RETRY','BILLING_RECONCILIATION') AND candidate.status IN ('PENDING','RETRY','RUNNING') AND (candidate.next_attempt_at IS NULL OR candidate.next_attempt_at <= now()) AND (candidate.lease_until IS NULL OR candidate.lease_until < now()) AND NOT EXISTS (SELECT 1 FROM financial_sync_jobs active WHERE active.id <> candidate.id AND active.tenant_id = candidate.tenant_id AND active.status = 'RUNNING' AND active.lease_until >= now()) AND NOT (candidate.dedupe_key LIKE 'OP:CHUNK:OP:RECENT:%' AND EXISTS (SELECT 1 FROM financial_sync_jobs history WHERE history.tenant_id = candidate.tenant_id AND history.client_id = candidate.client_id AND history.seller_id = candidate.seller_id AND history.dedupe_key LIKE 'OP:CHUNK:OP:HISTORY:%' AND history.status IN ('PENDING','RETRY','RUNNING'))) ORDER BY CASE WHEN candidate.job_type IN ('OPERATIONAL_SYNC_CHUNK','OPERATIONAL_SYNC_GAP_RETRY') THEN 0 ELSE 1 END, candidate.range_to DESC, candidate.updated_at, candidate.seller_id, candidate.created_at FOR UPDATE OF candidate SKIP LOCKED LIMIT 1").FirstOrDefaultAsync(cancellationToken)
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
                    queryFrom, segmentTo, pageCheckpoint.Offset, OperationalHistoryPageSize, cancellationToken);
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
        var windows = children.Where(x => x.JobType == FinancialSyncJobTypes.OperationalSyncChunk
            && !IsSkippedWindow(x.Status)).ToList();
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

    private static DateTimeOffset StartOfUtcDay(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
    }

    private static bool IsSkippedWindow(string status) =>
        status.StartsWith("SKIPPED_", StringComparison.Ordinal);

    private static HistoryBoundaryPlan? ParseHistoryBoundaryPlan(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<HistoryBoundaryPlan>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

    private sealed class HistoryBoundaryPlan
    {
        public string AlgorithmVersion { get; set; } = OperationalHistoryAlgorithm;
        public int OverlapHours { get; set; } = 1;
        public DateTimeOffset DiscoveryFrom { get; set; }
        public DateTimeOffset DiscoveryTo { get; set; }
        public DateTimeOffset? FirstOrderAt { get; set; }
        public DateTimeOffset WorkFrom { get; set; }
        public DateTimeOffset? DiscoveryCompletedAt { get; set; }
        public long RemoteReportedTotal { get; set; }
    }

    private static async Task<IDisposable> AcquireEnqueueGatesAsync(
        string tenantId, Guid clientId, IReadOnlyCollection<long> sellers, CancellationToken cancellationToken)
    {
        var held = new List<SemaphoreSlim>(sellers.Count);
        try
        {
            foreach (var seller in sellers)
            {
                var key = $"{tenantId}:{clientId:N}:{seller}";
                var gate = EnqueueGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(cancellationToken);
                held.Add(gate);
            }
            return new EnqueueGateLease(held);
        }
        catch
        {
            for (var index = held.Count - 1; index >= 0; index--)
                held[index].Release();
            throw;
        }
    }

    private sealed class EnqueueGateLease(IReadOnlyList<SemaphoreSlim> held) : IDisposable
    {
        public void Dispose()
        {
            for (var index = held.Count - 1; index >= 0; index--)
                held[index].Release();
        }
    }

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
