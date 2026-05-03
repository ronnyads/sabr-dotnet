using Phub.Domain.Enums;

namespace Phub.Application.Models;

public sealed class UserUpdateRequest
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Password { get; set; }

    public UserRole Role { get; set; } = UserRole.Admin;
    public string? SectorCode { get; set; }

    public bool IsActive { get; set; } = true;
}
