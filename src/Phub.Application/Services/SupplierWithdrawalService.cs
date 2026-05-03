using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class SupplierWithdrawalService
{
    private readonly IAppDbContext _dbContext;
    private readonly SupplierWalletService _walletService;

    public SupplierWithdrawalService(IAppDbContext dbContext, SupplierWalletService walletService)
    {
        _dbContext = dbContext;
        _walletService = walletService;
    }

    public async Task<ServiceResult<SupplierWithdrawalResult>> RequestAsync(
        Guid supplierId,
        SupplierWithdrawalRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.RequestedAmountCents <= 0)
        {
            return ServiceResult<SupplierWithdrawalResult>.Failure(new[]
            {
                new ValidationError("requestedAmountCents", "Amount must be greater than zero")
            });
        }

        var account = await _walletService.GetOrCreateAccountAsync(supplierId, cancellationToken);
        if (account.AvailableBalanceCents < request.RequestedAmountCents)
        {
            return ServiceResult<SupplierWithdrawalResult>.Failure(new[]
            {
                new ValidationError("requestedAmountCents", "Insufficient available balance")
            });
        }

        var config = await GetFinancialConfigAsync(cancellationToken);
        var fee = CalculateFee(request.RequestedAmountCents, config);
        var net = request.RequestedAmountCents - fee;

        var supplier = await _dbContext.Suppliers
            .FirstOrDefaultAsync(s => s.Id == supplierId, cancellationToken);

        var withdrawal = new SupplierWithdrawal
        {
            SupplierId = supplierId,
            RequestedAmountCents = request.RequestedAmountCents,
            FeeAmountCents = fee,
            NetAmountCents = net,
            Status = SupplierWithdrawalStatus.Pending,
            BankDetailsSnapshot = supplier?.BankInfo
        };

        account.AvailableBalanceCents -= request.RequestedAmountCents;
        account.BlockedBalanceCents += request.RequestedAmountCents;
        account.UpdatedAt = DateTimeOffset.UtcNow;

        var entry = new SupplierWalletEntry
        {
            SupplierId = supplierId,
            Type = WalletEntryType.Debit,
            AmountCents = request.RequestedAmountCents,
            BalanceAfterCents = account.PendingBalanceCents + account.BlockedBalanceCents + account.AvailableBalanceCents,
            Status = SupplierWalletEntryStatus.Withdrawing,
            ReferenceType = "withdrawal",
            ReferenceId = withdrawal.Id.ToString()
        };

        _dbContext.SupplierWithdrawals.Add(withdrawal);
        _dbContext.SupplierWalletEntries.Add(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<SupplierWithdrawalResult>.Success(MapResult(withdrawal));
    }

    public async Task<ServiceResult<SupplierWithdrawalsResult>> ListAsync(
        Guid supplierId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var query = _dbContext.SupplierWithdrawals
            .Where(w => w.SupplierId == supplierId)
            .OrderByDescending(w => w.RequestedAt);

        var total = await query.CountAsync(cancellationToken);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        return ServiceResult<SupplierWithdrawalsResult>.Success(new SupplierWithdrawalsResult
        {
            Items = items.Select(MapResult).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize
        });
    }

    public async Task<ServiceResult<SupplierWithdrawalResult>> GetAsync(
        Guid supplierId,
        Guid withdrawalId,
        CancellationToken cancellationToken = default)
    {
        var w = await _dbContext.SupplierWithdrawals
            .FirstOrDefaultAsync(w => w.Id == withdrawalId && w.SupplierId == supplierId, cancellationToken);

        if (w == null)
            return ServiceResult<SupplierWithdrawalResult>.NotFound("id", "Withdrawal not found");

        return ServiceResult<SupplierWithdrawalResult>.Success(MapResult(w));
    }

    public async Task<ServiceResult<List<SupplierWithdrawalResult>>> ListPendingAdminAsync(
        CancellationToken cancellationToken = default)
    {
        var items = await _dbContext.SupplierWithdrawals
            .Where(w => w.Status == SupplierWithdrawalStatus.Pending)
            .OrderBy(w => w.RequestedAt)
            .ToListAsync(cancellationToken);

        return ServiceResult<List<SupplierWithdrawalResult>>.Success(items.Select(MapResult).ToList());
    }

    public async Task<ServiceResult<SupplierWithdrawalResult>> ApproveAsync(
        Guid withdrawalId,
        Guid approverUserId,
        AdminApproveWithdrawalRequest request,
        CancellationToken cancellationToken = default)
    {
        var w = await _dbContext.SupplierWithdrawals
            .FirstOrDefaultAsync(w => w.Id == withdrawalId, cancellationToken);

        if (w == null)
            return ServiceResult<SupplierWithdrawalResult>.NotFound("id", "Withdrawal not found");

        if (w.Status != SupplierWithdrawalStatus.Pending)
        {
            return ServiceResult<SupplierWithdrawalResult>.Failure(new[]
            {
                new ValidationError("status", "Only pending withdrawals can be approved")
            });
        }

        var account = await _walletService.GetOrCreateAccountAsync(w.SupplierId, cancellationToken);
        account.BlockedBalanceCents -= w.RequestedAmountCents;
        account.UpdatedAt = DateTimeOffset.UtcNow;

        var entry = new SupplierWalletEntry
        {
            SupplierId = w.SupplierId,
            Type = WalletEntryType.Debit,
            AmountCents = w.RequestedAmountCents,
            BalanceAfterCents = account.PendingBalanceCents + account.BlockedBalanceCents + account.AvailableBalanceCents,
            Status = SupplierWalletEntryStatus.Withdrawn,
            ReferenceType = "withdrawal",
            ReferenceId = w.Id.ToString()
        };

        w.Status = SupplierWithdrawalStatus.Completed;
        w.ApprovedAt = DateTimeOffset.UtcNow;
        w.ApprovedByPlatformUserId = approverUserId == Guid.Empty ? null : approverUserId;
        w.Notes = request.Notes;

        _dbContext.SupplierWalletEntries.Add(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<SupplierWithdrawalResult>.Success(MapResult(w));
    }

    public async Task<ServiceResult<SupplierWithdrawalResult>> RejectAsync(
        Guid withdrawalId,
        AdminRejectWithdrawalRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return ServiceResult<SupplierWithdrawalResult>.Failure(new[]
            {
                new ValidationError("reason", "Rejection reason is required")
            });
        }

        var w = await _dbContext.SupplierWithdrawals
            .FirstOrDefaultAsync(w => w.Id == withdrawalId, cancellationToken);

        if (w == null)
            return ServiceResult<SupplierWithdrawalResult>.NotFound("id", "Withdrawal not found");

        if (w.Status != SupplierWithdrawalStatus.Pending)
        {
            return ServiceResult<SupplierWithdrawalResult>.Failure(new[]
            {
                new ValidationError("status", "Only pending withdrawals can be rejected")
            });
        }

        var account = await _walletService.GetOrCreateAccountAsync(w.SupplierId, cancellationToken);
        account.BlockedBalanceCents -= w.RequestedAmountCents;
        account.AvailableBalanceCents += w.RequestedAmountCents;
        account.UpdatedAt = DateTimeOffset.UtcNow;

        var entry = new SupplierWalletEntry
        {
            SupplierId = w.SupplierId,
            Type = WalletEntryType.Credit,
            AmountCents = w.RequestedAmountCents,
            BalanceAfterCents = account.PendingBalanceCents + account.BlockedBalanceCents + account.AvailableBalanceCents,
            Status = SupplierWalletEntryStatus.Available,
            ReferenceType = "withdrawal_rejection",
            ReferenceId = w.Id.ToString()
        };

        w.Status = SupplierWithdrawalStatus.Rejected;
        w.RejectedAt = DateTimeOffset.UtcNow;
        w.RejectionReason = request.Reason.Trim();

        _dbContext.SupplierWalletEntries.Add(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<SupplierWithdrawalResult>.Success(MapResult(w));
    }

    private async Task<PlatformFinancialConfig> GetFinancialConfigAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.PlatformFinancialConfigs
                   .OrderByDescending(c => c.UpdatedAt)
                   .FirstOrDefaultAsync(cancellationToken)
               ?? new PlatformFinancialConfig();
    }

    private static long CalculateFee(long amountCents, PlatformFinancialConfig config)
    {
        var percentFee = (long)Math.Ceiling(amountCents * (config.WithdrawalFeePercent / 100m));
        return percentFee + config.WithdrawalFeeFixedCents;
    }

    private static SupplierWithdrawalResult MapResult(SupplierWithdrawal w) => new()
    {
        Id = w.Id,
        SupplierId = w.SupplierId,
        RequestedAmountCents = w.RequestedAmountCents,
        FeeAmountCents = w.FeeAmountCents,
        NetAmountCents = w.NetAmountCents,
        Status = w.Status.ToString(),
        BankDetailsSnapshot = w.BankDetailsSnapshot,
        RequestedAt = w.RequestedAt,
        ApprovedAt = w.ApprovedAt,
        RejectedAt = w.RejectedAt,
        RejectionReason = w.RejectionReason,
        Notes = w.Notes
    };
}
