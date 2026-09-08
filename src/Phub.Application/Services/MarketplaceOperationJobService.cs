using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class MarketplaceOperationJobService
{
    public const string PullLabels = "PULL_LABELS";
    private readonly IAppDbContext _db;
    private readonly OrderFulfillmentService _fulfillment;

    public MarketplaceOperationJobService(IAppDbContext db, OrderFulfillmentService fulfillment)
    {
        _db = db;
        _fulfillment = fulfillment;
    }

    public async Task<MarketplaceOperationJobResult> EnqueueLabelPullAsync(
        string tenantId, Guid clientId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
    {
        var ids = orderIds.Where(x => x != Guid.Empty).Distinct().ToArray();
        var job = new MarketplaceOperationJob
        {
            TenantId = tenantId,
            ClientId = clientId,
            Provider = MarketplaceProvider.MercadoLivre,
            OperationType = PullLabels,
            PayloadJson = JsonSerializer.Serialize(ids),
            Total = ids.Length
        };
        _db.MarketplaceOperationJobs.Add(job);
        await _db.SaveChangesAsync(cancellationToken);
        return Map(job);
    }

    public async Task<MarketplaceOperationJobResult?> GetAsync(
        Guid jobId, string tenantId, Guid clientId, CancellationToken cancellationToken)
    {
        var job = await _db.MarketplaceOperationJobs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == jobId && x.TenantId == tenantId && x.ClientId == clientId, cancellationToken);
        return job == null ? null : Map(job);
    }

    public async Task ProcessPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        var ids = await _db.MarketplaceOperationJobs.AsNoTracking()
            .Where(x => x.Status == "PENDING" && x.OperationType == PullLabels)
            .OrderBy(x => x.CreatedAt)
            .Select(x => x.Id)
            .Take(Math.Clamp(batchSize, 1, 25))
            .ToListAsync(cancellationToken);

        foreach (var id in ids)
        {
            var now = DateTimeOffset.UtcNow;
            var claimed = await _db.MarketplaceOperationJobs
                .Where(x => x.Id == id && x.Status == "PENDING")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, "PROCESSING")
                    .SetProperty(x => x.StartedAt, now)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.Attempts, x => x.Attempts + 1), cancellationToken);
            if (claimed == 0) continue;

            var job = await _db.MarketplaceOperationJobs.FirstAsync(x => x.Id == id, cancellationToken);
            try
            {
                var orderIds = JsonSerializer.Deserialize<Guid[]>(job.PayloadJson) ?? [];
                var result = await _fulfillment.PullLabelsBulkAsync(job.TenantId, job.ClientId, orderIds, cancellationToken);
                if (!result.Succeeded || result.Data == null)
                    throw new InvalidOperationException(string.Join("; ", result.Errors));

                job.ResultJson = JsonSerializer.Serialize(result.Data);
                job.Total = result.Data.Total;
                job.Processed = result.Data.Total;
                job.Succeeded = result.Data.Succeeded;
                job.Failed = result.Data.Failed;
                job.Status = result.Data.Failed == 0 ? "COMPLETED" : "COMPLETED_WITH_ERRORS";
                job.CompletedAt = DateTimeOffset.UtcNow;
                job.LastError = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                job.Status = job.Attempts < 3 ? "PENDING" : "FAILED";
                job.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                if (job.Status == "FAILED") job.CompletedAt = DateTimeOffset.UtcNow;
            }
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private static MarketplaceOperationJobResult Map(MarketplaceOperationJob job) => new()
    {
        JobId = job.Id,
        OperationType = job.OperationType,
        Status = job.Status,
        Total = job.Total,
        Processed = job.Processed,
        Succeeded = job.Succeeded,
        Failed = job.Failed,
        LastError = job.LastError,
        CreatedAt = job.CreatedAt,
        CompletedAt = job.CompletedAt
    };
}
