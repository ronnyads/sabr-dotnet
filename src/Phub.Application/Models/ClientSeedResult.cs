using Phub.Domain.Enums;

namespace Phub.Application.Models;

public sealed class ClientSeedResult
{
    public Guid ClientId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string ProtheusCode { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public ClientStatus Status { get; set; }
}
