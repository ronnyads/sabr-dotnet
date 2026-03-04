using Sabr.Domain.Common;
using Sabr.Domain.Enums;

namespace Sabr.Domain.Entities;

public sealed class User : EntityBase
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.Admin;

    public string? SectorCode { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastLoginAt { get; set; }
}
