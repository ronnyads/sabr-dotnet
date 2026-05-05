using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Phub.Api.Models;
using Phub.Api.Security;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Options;
using Phub.Application.Services;
using Phub.Domain.Enums;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Phub.Api.Controllers;

[ApiController]
[Route("api/v1/admin/auth")]
public sealed class AdminAuthController : ControllerBase
{
    private readonly AuthService _authService;
    private readonly JwtOptions _jwtOptions;
    private readonly RefreshTokenOptions _refreshOptions;
    private readonly LoginAttemptService _loginAttemptService;

    public AdminAuthController(
        AuthService authService,
        IOptions<JwtOptions> jwtOptions,
        IOptions<RefreshTokenOptions> refreshOptions,
        LoginAttemptService loginAttemptService)
    {
        _authService = authService;
        _jwtOptions = jwtOptions.Value;
        _refreshOptions = refreshOptions.Value;
        _loginAttemptService = loginAttemptService;
    }

    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] AuthLoginRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        var ip = GetClientIp() ?? "unknown";
        if (_loginAttemptService.TryGetLock("platform", email, ip, out var retryAfter))
        {
            Response.Headers.Append("Retry-After", retryAfter.ToString());
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = "Too many login attempts. Try again later.",
                retryAfterSeconds = retryAfter,
                source = "credential-lock"
            });
        }

        var result = await _authService.LoginPlatformAsync(request, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            _loginAttemptService.RegisterFailure("platform", email, ip);
            return Unauthorized(new { errors = result.Errors });
        }

        _loginAttemptService.RegisterSuccess("platform", email, ip);

        result.Data.Scope = "platform";
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_jwtOptions.AccessTokenMinutes);
        var token = GenerateToken(result.Data, expiresAt.UtcDateTime, includeTenant: false);
        var refreshToken = await _authService.IssuePlatformRefreshTokenAsync(
            result.Data.Id,
            _refreshOptions.Days,
            GetClientIp(),
            Request.Headers.UserAgent.ToString(),
            cancellationToken);

        SetRefreshCookie(refreshToken);

        return Ok(new AuthLoginResponse
        {
            AccessToken = token,
            ExpiresAt = expiresAt,
            AccountType = result.Data.AccountType,
            User = result.Data,
            RefreshToken = refreshToken
        });
    }

    [AllowAnonymous]
    [HttpGet("csrf")]
    public IActionResult Csrf()
    {
        var token = GenerateCsrfToken();
        var options = new CookieOptions
        {
            HttpOnly = false,
            Secure = _refreshOptions.RequireHttps,
            SameSite = SameSiteMode.None,
            Expires = DateTimeOffset.UtcNow.AddDays(_refreshOptions.Days),
            Path = "/"
        };

        if (!string.IsNullOrWhiteSpace(_refreshOptions.CookieDomain))
        {
            options.Domain = _refreshOptions.CookieDomain.Trim();
        }

        Response.Cookies.Append(CsrfMiddleware.AdminCookieName, token, options);
        return Ok(new { token });
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest? request, CancellationToken cancellationToken)
    {
        var refreshToken =
            Request.Cookies[_refreshOptions.CookieName] ??
            request?.RefreshToken ??
            Request.Headers["X-Refresh-Token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Unauthorized(new { error = "Refresh token missing" });
        }

        var result = await _authService.RefreshPlatformAsync(
            refreshToken,
            GetClientIp(),
            Request.Headers.UserAgent.ToString(),
            _refreshOptions.Days,
            cancellationToken);

        if (!result.Succeeded || result.Data == null)
        {
            ClearRefreshCookie();
            return Unauthorized(new { errors = result.Errors });
        }

        result.Data.User.Scope = "platform";
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_jwtOptions.AccessTokenMinutes);
        var token = GenerateToken(result.Data.User, expiresAt.UtcDateTime, includeTenant: false);
        SetRefreshCookie(result.Data.RefreshToken);

        return Ok(new AuthLoginResponse
        {
            AccessToken = token,
            ExpiresAt = expiresAt,
            AccountType = result.Data.User.AccountType,
            User = result.Data.User
        });
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var refreshToken = Request.Cookies[_refreshOptions.CookieName];
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            await _authService.RevokeRefreshTokenAsync("platform", refreshToken, cancellationToken);
        }

        ClearRefreshCookie();
        return NoContent();
    }

    private void SetRefreshCookie(string token)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = _refreshOptions.RequireHttps,
            SameSite = SameSiteMode.None,
            Expires = DateTimeOffset.UtcNow.AddDays(_refreshOptions.Days),
            // Scope to entire admin/auth API endpoint tree to allow both login and refresh
            Path = "/api/v1/admin"
        };

        Response.Cookies.Append(_refreshOptions.CookieName, token, options);
    }

    private void ClearRefreshCookie()
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = _refreshOptions.RequireHttps,
            SameSite = SameSiteMode.None,
            Expires = DateTimeOffset.UtcNow.AddDays(-1),
            Path = "/api/v1/admin"
        };

        Response.Cookies.Append(_refreshOptions.CookieName, string.Empty, options);
    }

    private string? GetClientIp()
    {
        if (Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded))
        {
            var value = forwarded.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    return parts[0].Trim();
                }
            }
        }

        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    private string GenerateToken(AuthUserResult user, DateTime expiresAtUtc, bool includeTenant)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.Key));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("accountType", user.AccountType),
            new("scope", user.Scope ?? "platform")
        };

        if (includeTenant)
        {
            claims.Add(new Claim("tenantId", user.TenantId));
        }

        if (user.Role.HasValue)
        {
            claims.Add(new Claim(ClaimTypes.Role, GetRoleName(user.Role.Value)));
        }

        if (!string.IsNullOrWhiteSpace(user.SectorCode))
        {
            claims.Add(new Claim("sectorCode", user.SectorCode));
        }

        var token = new JwtSecurityToken(
            issuer: _jwtOptions.Issuer,
            audience: _jwtOptions.Audience,
            claims: claims,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string GetRoleName(UserRole role)
    {
        return role switch
        {
            UserRole.SuperAdmin => "SuperAdmin",
            UserRole.Finance => "Finance",
            UserRole.Admin => "Admin",
            _ => "Admin"
        };
    }

    private static string GenerateCsrfToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var base64 = Convert.ToBase64String(bytes);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
