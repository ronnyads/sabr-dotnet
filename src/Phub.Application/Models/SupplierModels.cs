namespace Phub.Application.Models;

public sealed class SupplierRegisterRequest
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public string? Phone { get; set; }
    public string? Document { get; set; }
    public string? TenantId { get; set; }
}

public sealed class SupplierResult
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public string? Phone { get; set; }
    public string? Document { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}

public sealed class PlatformFinancialConfigResult
{
    public Guid Id { get; set; }
    public decimal DefaultMarginPercent { get; set; }
    public decimal WithdrawalFeePercent { get; set; }
    public long WithdrawalFeeFixedCents { get; set; }
    public int SettlementDelayDays { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class UpdatePlatformFinancialConfigRequest
{
    public decimal DefaultMarginPercent { get; set; }
    public decimal WithdrawalFeePercent { get; set; }
    public long WithdrawalFeeFixedCents { get; set; }
    public int SettlementDelayDays { get; set; }
}
