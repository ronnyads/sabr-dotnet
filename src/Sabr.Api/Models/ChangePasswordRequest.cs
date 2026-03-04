namespace Sabr.Api.Models;

public sealed class ChangePasswordRequest
{
    public string NewPassword { get; set; } = string.Empty;
}
