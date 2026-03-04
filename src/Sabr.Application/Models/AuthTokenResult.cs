namespace Sabr.Application.Models;

public sealed class AuthTokenResult
{
    public AuthUserResult User { get; set; } = new();
    public string RefreshToken { get; set; } = string.Empty;
}
