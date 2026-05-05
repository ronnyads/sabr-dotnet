using Phub.Domain.Enums;

namespace Phub.Application.Models;

public sealed class UserResult
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public UserRole Role { get; set; }
    public string? SectorCode { get; set; }

    public bool IsActive { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}
