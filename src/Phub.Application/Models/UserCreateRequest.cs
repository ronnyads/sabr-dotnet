using Phub.Domain.Enums;

namespace Phub.Application.Models;

public sealed class UserCreateRequest
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.Admin;
    public string? SectorCode { get; set; }

    public bool IsActive { get; set; } = true;
}
