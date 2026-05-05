namespace Phub.Application.Models;

public sealed class PlatformUserResetPasswordRequest
{
    public string TemporaryPassword { get; set; } = string.Empty;
}
