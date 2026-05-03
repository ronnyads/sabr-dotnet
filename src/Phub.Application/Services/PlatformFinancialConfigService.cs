using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
using Phub.Domain.Entities;

namespace Phub.Application.Services;

public sealed class PlatformFinancialConfigService
{
    private readonly IAppDbContext _dbContext;

    public PlatformFinancialConfigService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ServiceResult<PlatformFinancialConfigResult>> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var config = await _dbContext.PlatformFinancialConfigs
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (config == null)
        {
            config = new PlatformFinancialConfig();
        }

        return ServiceResult<PlatformFinancialConfigResult>.Success(MapResult(config));
    }

    public async Task<ServiceResult<PlatformFinancialConfigResult>> UpdateAsync(
        UpdatePlatformFinancialConfigRequest request,
        Guid updatedByUserId,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<ValidationError>();
        if (request.DefaultMarginPercent < 0 || request.DefaultMarginPercent > 100)
            errors.Add(new ValidationError("defaultMarginPercent", "Margin must be between 0 and 100"));
        if (request.WithdrawalFeePercent < 0 || request.WithdrawalFeePercent > 100)
            errors.Add(new ValidationError("withdrawalFeePercent", "Fee percent must be between 0 and 100"));
        if (request.WithdrawalFeeFixedCents < 0)
            errors.Add(new ValidationError("withdrawalFeeFixedCents", "Fixed fee cannot be negative"));
        if (request.SettlementDelayDays < 0)
            errors.Add(new ValidationError("settlementDelayDays", "Settlement delay cannot be negative"));
        if (errors.Count > 0)
            return ServiceResult<PlatformFinancialConfigResult>.Failure(errors);

        var config = await _dbContext.PlatformFinancialConfigs
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (config == null)
        {
            config = new PlatformFinancialConfig();
            _dbContext.PlatformFinancialConfigs.Add(config);
        }

        config.DefaultMarginPercent = request.DefaultMarginPercent;
        config.WithdrawalFeePercent = request.WithdrawalFeePercent;
        config.WithdrawalFeeFixedCents = request.WithdrawalFeeFixedCents;
        config.SettlementDelayDays = request.SettlementDelayDays;
        config.UpdatedAt = DateTimeOffset.UtcNow;
        config.UpdatedByPlatformUserId = updatedByUserId == Guid.Empty ? null : updatedByUserId;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<PlatformFinancialConfigResult>.Success(MapResult(config));
    }

    private static PlatformFinancialConfigResult MapResult(PlatformFinancialConfig c) => new()
    {
        Id = c.Id,
        DefaultMarginPercent = c.DefaultMarginPercent,
        WithdrawalFeePercent = c.WithdrawalFeePercent,
        WithdrawalFeeFixedCents = c.WithdrawalFeeFixedCents,
        SettlementDelayDays = c.SettlementDelayDays,
        UpdatedAt = c.UpdatedAt
    };
}
