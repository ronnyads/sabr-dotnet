using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;
using Sabr.Infrastructure.Persistence;

namespace Sabr.Infrastructure.Services;

public sealed class ProtheusOutboxWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProtheusOutboxWorker> _logger;

    public ProtheusOutboxWorker(IServiceScopeFactory scopeFactory, ILogger<ProtheusOutboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var processor = scope.ServiceProvider.GetRequiredService<IProtheusOutboxProcessor>();

                var claimedIds = await ClaimBatchAsync(db, 50, stoppingToken);
                if (claimedIds.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                var items = await db.ProtheusOutboxEvents
                    .Where(e => claimedIds.Contains(e.Id))
                    .ToListAsync(stoppingToken);

                foreach (var item in items)
                {
                    try
                    {
                        await processor.ProcessAsync(item, stoppingToken);
                        item.Status = OutboxStatus.Processed;
                        item.ProcessedAt = DateTimeOffset.UtcNow;
                        item.NextRetryAt = null;
                        item.LastError = null;
                    }
                    catch (Exception ex)
                    {
                        var attempts = item.Attempts;
                        item.LastError = ex.Message;

                        if (attempts >= 10)
                        {
                            item.Status = OutboxStatus.DeadLetter;
                            db.AuditEvents.Add(new AuditEvent
                            {
                                TenantId = item.TenantId,
                                ActorType = "System",
                                ActorId = null,
                                Action = "Outbox.DeadLetter",
                                Entity = nameof(ProtheusOutboxEvent),
                                EntityId = item.Id,
                                RequestId = Guid.NewGuid(),
                                MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
                                {
                                    item.EventType,
                                    item.AggregateType,
                                    item.AggregateId,
                                    item.Attempts,
                                    item.LastError
                                })
                            });
                        }
                        else
                        {
                            item.Status = OutboxStatus.Retry;
                            item.NextRetryAt = DateTimeOffset.UtcNow.Add(Backoff(attempts));
                        }

                        _logger.LogWarning(ex, "Outbox processing failed for {OutboxId}", item.Id);
                    }
                }

                await db.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in ProtheusOutboxWorker");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private static TimeSpan Backoff(int attempts)
    {
        var seconds = attempts switch
        {
            <= 1 => 30,
            2 => 60,
            3 => 120,
            _ => Math.Min(1800, 120 * (int)Math.Pow(2, Math.Min(attempts - 3, 4)))
        };

        return TimeSpan.FromSeconds(seconds);
    }

    private static async Task<List<Guid>> ClaimBatchAsync(AppDbContext db, int limit, CancellationToken cancellationToken)
    {
        var claimedIds = new List<Guid>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
            selectCommand.CommandText = @"
                SELECT ""Id""
                FROM protheus_outbox
                WHERE (status = 'Pending' OR status = 'Retry')
                  AND (next_retry_at IS NULL OR next_retry_at <= now())
                ORDER BY created_at
                FOR UPDATE SKIP LOCKED
                LIMIT @limit";
            selectCommand.Parameters.AddWithValue("limit", limit);

            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                claimedIds.Add(reader.GetGuid(0));
            }
        }

        if (claimedIds.Count > 0)
        {
            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
            updateCommand.CommandText = @"
                UPDATE protheus_outbox
                SET status = 'Processing',
                    attempts = attempts + 1,
                    updated_at = now()
                WHERE ""Id"" = ANY(@ids)";
            updateCommand.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                Value = claimedIds.ToArray()
            });

            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return claimedIds;
    }
}
