using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class SupplierWalletService
{
    private readonly IAppDbContext _dbContext;

    public SupplierWalletService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ServiceResult<SupplierWalletSummaryResult>> GetSummaryAsync(
        Guid supplierId,
        CancellationToken cancellationToken = default)
    {
        var account = await GetOrCreateAccountAsync(supplierId, cancellationToken);
        return ServiceResult<SupplierWalletSummaryResult>.Success(MapSummary(account));
    }

    public async Task<ServiceResult<SupplierWalletEntriesResult>> GetEntriesAsync(
        Guid supplierId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var query = _dbContext.SupplierWalletEntries
            .Where(e => e.SupplierId == supplierId)
            .OrderByDescending(e => e.CreatedAt);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return ServiceResult<SupplierWalletEntriesResult>.Success(new SupplierWalletEntriesResult
        {
            Items = items.Select(MapEntry).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize
        });
    }

    public async Task<ServiceResult<SupplierWalletEntryResult>> CreditPendingAsync(
        Guid supplierId,
        Guid? orderId,
        long amountCents,
        int settlementDelayDays,
        string? referenceType = null,
        string? referenceId = null,
        CancellationToken cancellationToken = default)
    {
        if (amountCents <= 0)
        {
            return ServiceResult<SupplierWalletEntryResult>.Failure(new[]
            {
                new ValidationError("amountCents", "Amount must be greater than zero")
            });
        }

        var account = await GetOrCreateAccountAsync(supplierId, cancellationToken);
        account.PendingBalanceCents += amountCents;
        account.UpdatedAt = DateTimeOffset.UtcNow;

        var entry = new SupplierWalletEntry
        {
            SupplierId = supplierId,
            OrderId = orderId,
            Type = WalletEntryType.Credit,
            AmountCents = amountCents,
            BalanceAfterCents = account.PendingBalanceCents + account.BlockedBalanceCents + account.AvailableBalanceCents,
            Status = SupplierWalletEntryStatus.Pending,
            ReferenceType = referenceType,
            ReferenceId = referenceId,
            ScheduledAvailableAt = DateTimeOffset.UtcNow.AddDays(settlementDelayDays)
        };

        _dbContext.SupplierWalletEntries.Add(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<SupplierWalletEntryResult>.Success(MapEntry(entry));
    }

    public async Task<ServiceResult<bool>> SettlePendingEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        var dueEntries = await _dbContext.SupplierWalletEntries
            .Where(e => e.Status == SupplierWalletEntryStatus.Pending
                     && e.ScheduledAvailableAt != null
                     && e.ScheduledAvailableAt <= now
                     && e.Type == WalletEntryType.Credit)
            .ToListAsync(cancellationToken);

        if (dueEntries.Count == 0)
            return ServiceResult<bool>.Success(true);

        var supplierIds = dueEntries.Select(e => e.SupplierId).Distinct().ToList();
        var accounts = await _dbContext.SupplierWalletAccounts
            .Where(a => supplierIds.Contains(a.SupplierId))
            .ToListAsync(cancellationToken);

        var accountMap = accounts.ToDictionary(a => a.SupplierId);

        foreach (var entry in dueEntries)
        {
            if (!accountMap.TryGetValue(entry.SupplierId, out var account))
                continue;

            account.PendingBalanceCents -= entry.AmountCents;
            account.AvailableBalanceCents += entry.AmountCents;
            account.UpdatedAt = now;
            entry.Status = SupplierWalletEntryStatus.Available;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<bool>> ReverseOrderCreditAsync(
        Guid supplierId,
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var entries = await _dbContext.SupplierWalletEntries
            .Where(e => e.SupplierId == supplierId
                     && e.OrderId == orderId
                     && e.Type == WalletEntryType.Credit
                     && (e.Status == SupplierWalletEntryStatus.Pending || e.Status == SupplierWalletEntryStatus.Available))
            .ToListAsync(cancellationToken);

        if (entries.Count == 0)
            return ServiceResult<bool>.Success(true);

        var account = await GetOrCreateAccountAsync(supplierId, cancellationToken);

        foreach (var entry in entries)
        {
            if (entry.Status == SupplierWalletEntryStatus.Pending)
                account.PendingBalanceCents -= entry.AmountCents;
            else
                account.AvailableBalanceCents -= entry.AmountCents;

            entry.Status = SupplierWalletEntryStatus.Reversed;
        }

        account.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    public async Task<SupplierWalletAccount> GetOrCreateAccountAsync(
        Guid supplierId,
        CancellationToken cancellationToken = default)
    {
        var account = await _dbContext.SupplierWalletAccounts
            .FirstOrDefaultAsync(a => a.SupplierId == supplierId, cancellationToken);

        if (account != null)
            return account;

        account = new SupplierWalletAccount { SupplierId = supplierId };
        _dbContext.SupplierWalletAccounts.Add(account);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return account;
    }

    private static SupplierWalletSummaryResult MapSummary(SupplierWalletAccount a) => new()
    {
        SupplierId = a.SupplierId,
        PendingBalanceCents = a.PendingBalanceCents,
        BlockedBalanceCents = a.BlockedBalanceCents,
        AvailableBalanceCents = a.AvailableBalanceCents,
        UpdatedAt = a.UpdatedAt
    };

    private static SupplierWalletEntryResult MapEntry(SupplierWalletEntry e) => new()
    {
        Id = e.Id,
        SupplierId = e.SupplierId,
        OrderId = e.OrderId,
        Type = e.Type.ToString(),
        AmountCents = e.AmountCents,
        BalanceAfterCents = e.BalanceAfterCents,
        Status = e.Status.ToString(),
        ReferenceType = e.ReferenceType,
        ReferenceId = e.ReferenceId,
        ScheduledAvailableAt = e.ScheduledAvailableAt,
        CreatedAt = e.CreatedAt
    };
}
