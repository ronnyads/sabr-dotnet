namespace Phub.Domain.Entities;

public sealed class PlatformFinancialConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? TenantId { get; set; }
    public decimal DefaultMarginPercent { get; set; }
    public decimal WithdrawalFeePercent { get; set; }
    public long WithdrawalFeeFixedCents { get; set; }
    public int SettlementDelayDays { get; set; } = 30;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedByPlatformUserId { get; set; }
}
