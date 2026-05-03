namespace Phub.Application.Models;

public sealed class ClientSeedRequest
{
    public string AccountName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string TemporaryPassword { get; set; } = string.Empty;
}
