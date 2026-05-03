using Phub.Domain.Enums;

namespace Phub.Application.Models;

public sealed class BootstrapAdminRequest
{
    public string TenantSlug { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.SuperAdmin;
    public string SectorCode { get; set; } = string.Empty;
    public string AdminKey { get; set; } = string.Empty;
}
