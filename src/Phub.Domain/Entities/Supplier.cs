using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

public sealed class Supplier
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string EmailNormalized { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public SupplierStatus Status { get; set; } = SupplierStatus.PendingApproval;
    public string? LegalName { get; set; }
    public string? Document { get; set; }
    public string? CompanyName { get; set; }
    public string? Phone { get; set; }
    public string? BankInfo { get; set; }
    public bool IsActive { get; set; } = false;
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
