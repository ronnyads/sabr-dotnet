namespace Sabr.Application.Models;

public sealed class ClientResetPasswordResult
{
    public Guid ClientId { get; set; }
    public string TemporaryPassword { get; set; } = string.Empty;
}
