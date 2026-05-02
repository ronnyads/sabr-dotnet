using Sabr.Application.Models;

namespace Sabr.Api.Models;

public sealed class AuthLoginResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string TokenType { get; set; } = "Bearer";
    public DateTimeOffset ExpiresAt { get; set; }
    public string AccountType { get; set; } = "admin";
    public AuthUserResult User { get; set; } = new();
    // Optional refresh token for clients that cannot use cookies (fallback).
    public string? RefreshToken { get; set; }
}
