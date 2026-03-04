using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Validation;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;

namespace Sabr.Application.Services;

public sealed class WalletService
{
    private const string DebitScope = "wallet:debit";
    private const string CreditScope = "wallet:credit";

    private readonly IAppDbContext _dbContext;

    public WalletService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<ServiceResult<WalletOperationResult>> CreditAsync(
        string tenantId,
        WalletCreditRequest request,
        string idempotencyKey,
        Guid requestId,
        Guid? actorId,
        string actorType,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            tenantId,
            request.ClientId,
            request.AmountCents,
            WalletEntryType.Credit,
            request.ReferenceType,
            request.ReferenceId,
            allowNegative: true,
            scope: CreditScope,
            idempotencyKey,
            requestHashPayload: request,
            requestId,
            actorId,
            actorType,
            cancellationToken);
    }

    public Task<ServiceResult<WalletOperationResult>> DebitAsync(
        string tenantId,
        WalletDebitRequest request,
        string idempotencyKey,
        Guid requestId,
        Guid? actorId,
        string actorType,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            tenantId,
            request.ClientId,
            request.AmountCents,
            WalletEntryType.Debit,
            request.ReferenceType,
            request.ReferenceId,
            request.AllowNegative,
            DebitScope,
            idempotencyKey,
            request,
            requestId,
            actorId,
            actorType,
            cancellationToken);
    }

    private async Task<ServiceResult<WalletOperationResult>> ExecuteAsync(
        string tenantId,
        Guid clientId,
        long amountCents,
        WalletEntryType entryType,
        string? referenceType,
        string? referenceId,
        bool allowNegative,
        string scope,
        string idempotencyKey,
        object requestHashPayload,
        Guid requestId,
        Guid? actorId,
        string actorType,
        CancellationToken cancellationToken)
    {
        var errors = ValidateInput(tenantId, clientId, amountCents, idempotencyKey);
        if (errors.Count > 0)
        {
            return ServiceResult<WalletOperationResult>.Failure(errors);
        }

        var now = DateTimeOffset.UtcNow;
        var hash = HashRequest(scope, requestHashPayload);
        var idem = await EnsureIdempotencyAsync(tenantId, scope, idempotencyKey, hash, now, cancellationToken);

        if (idem == null)
        {
            return ServiceResult<WalletOperationResult>.Failure(new[]
            {
                new ValidationError("idempotency", "Idempotency key in progress")
            });
        }

        if (!string.Equals(idem.RequestHash, hash, StringComparison.Ordinal))
        {
            return ServiceResult<WalletOperationResult>.Failure(new[]
            {
                new ValidationError("idempotency", "Idempotency key payload mismatch")
            });
        }

        if (idem.Status == IdempotencyStatus.Completed && !string.IsNullOrWhiteSpace(idem.ResponseJson))
        {
            var cached = JsonSerializer.Deserialize<WalletOperationResult>(idem.ResponseJson);
            if (cached != null)
            {
                return ServiceResult<WalletOperationResult>.Success(cached);
            }
        }

        if (idem.Status == IdempotencyStatus.Started && idem.ResponseJson != null)
        {
            return ServiceResult<WalletOperationResult>.Failure(new[]
            {
                new ValidationError("idempotency", "Idempotency key in progress")
            });
        }

        var transaction = await ((DbContext)_dbContext).Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await ((DbContext)_dbContext).Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO wallet_accounts (id, tenant_id, client_id, balance_cents, updated_at)
                VALUES ({Guid.NewGuid()}, {tenantId}, {clientId}, 0, {DateTimeOffset.UtcNow})
                ON CONFLICT (tenant_id, client_id) DO NOTHING;", cancellationToken);

            var account = await _dbContext.WalletAccounts
                .FromSqlInterpolated($@"
                    SELECT id, tenant_id, client_id, balance_cents, updated_at
                    FROM wallet_accounts
                    WHERE tenant_id = {tenantId} AND client_id = {clientId}
                    FOR UPDATE")
                .AsTracking()
                .SingleAsync(cancellationToken);

            var delta = entryType == WalletEntryType.Debit ? -amountCents : amountCents;
            var resultingBalance = account.BalanceCents + delta;

            if (entryType == WalletEntryType.Debit && !allowNegative && resultingBalance < 0)
            {
                idem.Status = IdempotencyStatus.Failed;
                idem.ResponseJson = null;
                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return ServiceResult<WalletOperationResult>.Failure(new[]
                {
                    new ValidationError("balance", "Insufficient balance")
                });
            }

            account.BalanceCents = resultingBalance;
            account.UpdatedAt = DateTimeOffset.UtcNow;

            var ledger = new WalletLedgerEntry
            {
                TenantId = tenantId,
                ClientId = clientId,
                Type = entryType,
                AmountCents = amountCents,
                BalanceAfterCents = resultingBalance,
                RequestId = requestId,
                ReferenceType = referenceType,
                ReferenceId = referenceId
            };

            _dbContext.WalletLedgerEntries.Add(ledger);

            var result = new WalletOperationResult
            {
                Status = "Approved",
                BalanceAfterCents = resultingBalance,
                LedgerId = ledger.Id,
                RequestId = requestId
            };

            idem.Status = IdempotencyStatus.Completed;
            idem.ResponseJson = JsonSerializer.Serialize(result);

            _dbContext.AuditEvents.Add(new AuditEvent
            {
                TenantId = tenantId,
                ActorType = actorType,
                ActorId = actorId,
                Action = entryType == WalletEntryType.Debit ? "Wallet.Debit" : "Wallet.Credit",
                Entity = nameof(WalletAccount),
                EntityId = account.Id,
                RequestId = requestId,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    clientId,
                    amountCents,
                    entryType,
                    resultingBalance,
                    referenceType,
                    referenceId,
                    idempotencyKey
                })
            });

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<WalletOperationResult>.Success(result);
        }
        catch
        {
            idem.Status = IdempotencyStatus.Failed;
            idem.ResponseJson = null;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<IdempotencyKey?> EnsureIdempotencyAsync(
        string tenantId,
        string scope,
        string key,
        string hash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await _dbContext.IdempotencyKeys
            .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.Scope == scope && i.Key == key, cancellationToken);

        if (existing != null)
        {
            if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
            {
                return existing;
            }

            if (existing.Status == IdempotencyStatus.Started)
            {
                return new IdempotencyKey
                {
                    TenantId = existing.TenantId,
                    Scope = existing.Scope,
                    Key = existing.Key,
                    RequestHash = existing.RequestHash,
                    Status = existing.Status,
                    ResponseJson = "IN_PROGRESS"
                };
            }

            return existing;
        }

        var entity = new IdempotencyKey
        {
            TenantId = tenantId,
            Scope = scope,
            Key = key,
            RequestHash = hash,
            Status = IdempotencyStatus.Started,
            ExpiresAt = now.AddHours(24)
        };

        _dbContext.IdempotencyKeys.Add(entity);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return entity;
        }
        catch (DbUpdateException)
        {
            return await _dbContext.IdempotencyKeys
                .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.Scope == scope && i.Key == key, cancellationToken);
        }
    }

    private static List<ValidationError> ValidateInput(string tenantId, Guid clientId, long amountCents, string idempotencyKey)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            errors.Add(new ValidationError("tenantId", "Tenant is required"));
        }

        if (clientId == Guid.Empty)
        {
            errors.Add(new ValidationError("clientId", "ClientId is required"));
        }

        if (amountCents <= 0)
        {
            errors.Add(new ValidationError("amountCents", "Amount must be greater than zero"));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            errors.Add(new ValidationError("idempotency", "Idempotency-Key header is required"));
        }

        return errors;
    }

    private static string HashRequest(string scope, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes($"{scope}:{json}");
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
