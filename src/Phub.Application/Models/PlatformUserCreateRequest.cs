using Phub.Domain.Enums;

namespace Phub.Application.Models;

public sealed class PlatformUserCreateRequest
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public PlatformUserRole Role { get; set; } = PlatformUserRole.Admin;
    public bool IsActive { get; set; } = true;
}
