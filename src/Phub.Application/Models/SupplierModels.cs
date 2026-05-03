namespace Phub.Application.Models;

public sealed class SupplierRegisterRequest
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public string? Phone { get; set; }
    public string? Document { get; set; }
    public string? TenantId { get; set; }
}
