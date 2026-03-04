using Sabr.Domain.Enums;

namespace Sabr.Application.Models;

public sealed class ClientRegistrationResult
{
    public Guid ClientId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public ClientStatus Status { get; set; }
}
