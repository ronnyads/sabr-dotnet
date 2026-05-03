using Phub.Domain.Enums;

namespace Phub.Application.Models;

public sealed class AuthUserResult
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string? TenantSlug { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string AccountType { get; set; } = "admin";
    public string Scope { get; set; } = "tenant"; // tenant | platform
    public UserRole? Role { get; set; }
    public string? SectorCode { get; set; }
    public bool IsActive { get; set; }
    public bool? MustChangePassword { get; set; }
    public ClientStatus? Status { get; set; }
    public int? OnboardingStep { get; set; }
}
