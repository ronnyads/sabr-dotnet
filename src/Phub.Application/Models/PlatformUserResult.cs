using Phub.Domain.Enums;

namespace Phub.Application.Models;

public sealed class PlatformUserResult
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public PlatformUserRole Role { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}
