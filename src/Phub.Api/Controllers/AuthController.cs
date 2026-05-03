using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Phub.Api.Models;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Options;
using Phub.Application.Services;
using Phub.Api.Security;
using Phub.Domain.Enums;

namespace Phub.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly AuthService _authService;
    private readonly ITenantProvider _tenantProvider;
    private readonly JwtOptions _jwtOptions;
    private readonly BootstrapOptions _bootstrapOptions;
    private readonly RefreshTokenOptions _refreshOptions;
    private readonly LoginAttemptService _loginAttemptService;

    public AuthController(
        AuthService authService,
        ITenantProvider tenantProvider,
        IOptions<JwtOptions> jwtOptions,
        IOptions<BootstrapOptions> bootstrapOptions,
        IOptions<RefreshTokenOptions> refreshOptions,
        LoginAttemptService loginAttemptService)
    {
        _authService = authService;
        _tenantProvider = tenantProvider;
        _jwtOptions = jwtOptions.Value;
        _bootstrapOptions = bootstrapOptions.Value;
        _refreshOptions = refreshOptions.Value;
        _loginAttemptService = loginAttemptService;
    }

    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] AuthLoginRequest request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantProvider.TenantId;
        var isPlatform = _tenantProvider.IsPlatform;

        var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        var ip = GetClientIp() ?? "unknown";
        if (_loginAttemptService.TryGetLock("tenant", email, ip, out var retryAfter))
        {
            Response.Headers.Append("Retry-After", retryAfter.ToString());
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = "Too many login attempts. Try again later.",
                retryAfterSeconds = retryAfter,
                source = "credential-lock"
            });
        }

        var result = isPlatform
            ? await _authService.LoginPlatformAsync(request, cancellationToken)
            : string.IsNullOrWhiteSpace(tenantId)
                ? await _authService.LoginAutoTenantAsync(request, cancellationToken)
                : await _authService.LoginAsync(tenantId, request, cancellationToken);
        if (!result.Succeeded || result.Data == null)
        {
            _loginAttemptService.RegisterFailure("tenant", email, ip);

            if (result.Errors.Any(error =>
                    string.Equals(error.Field, "code", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(error.Message, AuthService.TenantAmbiguousCode, StringComparison.OrdinalIgnoreCase)))
            {
                return Conflict(new ApiError
                {
                    Code = AuthService.TenantAmbiguousCode,
                    Message = "Nao foi possivel identificar sua empresa automaticamente. Contate o suporte.",
                    TraceId = HttpContext.TraceIdentifier
                });
            }

            return Unauthorized(new { errors = result.Errors });
        }

        _loginAttemptService.RegisterSuccess("tenant", email, ip);

        if (string.IsNullOrWhiteSpace(result.Data.TenantSlug))
        {
            result.Data.TenantSlug = _tenantProvider.TenantSlug;
        }

        var resolvedTenantId = result.Data.TenantId;
        if (string.IsNullOrWhiteSpace(resolvedTenantId))
        {
            _loginAttemptService.RegisterFailure("tenant", email, ip);
            return Unauthorized(new
            {
                errors = new[]
                {
                    new { field = "credentials", message = "Invalid credentials" }
                }
            });
        }

        var accountType = result.Data.AccountType;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_jwtOptions.AccessTokenMinutes);
        result.Data.Scope = "tenant";
        var token = GenerateToken(result.Data, expiresAt.UtcDateTime);
        var refreshToken = string.Equals(accountType, AccountTypes.Client, StringComparison.OrdinalIgnoreCase)
            ? await _authService.IssueClientRefreshTokenAsync(
                result.Data.Id,
                resolvedTenantId,
                _refreshOptions.Days,
                GetClientIp(),
                Request.Headers.UserAgent.ToString(),
                cancellationToken)
            : await _authService.IssueRefreshTokenAsync(
                result.Data.Id,
                resolvedTenantId,
                _refreshOptions.Days,
                GetClientIp(),
                Request.Headers.UserAgent.ToString(),
                cancellationToken);

        SetRefreshCookie(refreshToken);

        return Ok(new AuthLoginResponse
        {
            AccessToken = token,
            ExpiresAt = expiresAt,
            AccountType = accountType,
            User = result.Data,
            RefreshToken = refreshToken
        });
    }

    [AllowAnonymous]
    [HttpGet("csrf")]
    public IActionResult Csrf()
    {
        var token = GenerateCsrfToken();
        var isPlatform = _tenantProvider.IsPlatform;
        var cookieName = isPlatform ? CsrfMiddleware.AdminCookieName : CsrfMiddleware.TenantCookieName;
        var headerName = isPlatform ? CsrfMiddleware.AdminHeaderName : CsrfMiddleware.TenantHeaderName;
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

        Response.Cookies.Append(cookieName, token, options);
        return Ok(new { token, header = headerName });
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest? request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new { error = "Tenant not resolved" });
        }

        var refreshToken =
            Request.Cookies[_refreshOptions.CookieName] ??
            request?.RefreshToken ??
            Request.Headers["X-Refresh-Token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Unauthorized(new { error = "Refresh token missing" });
        }

        var result = await _authService.RefreshAsync(
            tenantId,
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

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_jwtOptions.AccessTokenMinutes);
        result.Data.User.Scope = "tenant";
        var token = GenerateToken(result.Data.User, expiresAt.UtcDateTime);
        SetRefreshCookie(result.Data.RefreshToken);

        return Ok(new AuthLoginResponse
        {
            AccessToken = token,
            ExpiresAt = expiresAt,
            AccountType = result.Data.User.AccountType,
            User = result.Data.User
        });
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var tenantId = _tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest(new { error = "Tenant not resolved" });
        }

        var refreshToken = Request.Cookies[_refreshOptions.CookieName];
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            await _authService.RevokeRefreshTokenAsync(tenantId, refreshToken, cancellationToken);
        }

        ClearRefreshCookie();
        return NoContent();
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var accountType = User.FindFirst("accountType")?.Value;
        if (!string.Equals(accountType, AccountTypes.Client, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        var clientIdClaim = User.FindFirst("clientId")?.Value;
        if (!Guid.TryParse(clientIdClaim, out var clientId))
        {
            return Unauthorized(new { error = "Invalid client context" });
        }

        var result = await _authService.ChangeClientPasswordAsync(clientId, request.NewPassword, cancellationToken);
        if (!result.Succeeded)
        {
            return BadRequest(new { errors = result.Errors });
        }

        return Ok(new { success = true });
    }

    [AllowAnonymous]
    [HttpPost("bootstrap")]
    public async Task<IActionResult> BootstrapAdmin([FromBody] BootstrapAdminRequest request, CancellationToken cancellationToken)
    {
        if (!_bootstrapOptions.Enabled)
        {
            return Forbid();
        }

        if (!string.Equals(request.AdminKey, _bootstrapOptions.AdminKey, StringComparison.Ordinal))
        {
            return Unauthorized(new { error = "Invalid bootstrap key" });
        }

        var result = await _authService.BootstrapAdminAsync(request, cancellationToken);
        if (!result.Succeeded)
        {
            return BadRequest(new { errors = result.Errors });
        }

        return Ok(result.Data);
    }

    private void SetRefreshCookie(string token)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = _refreshOptions.RequireHttps,
            SameSite = SameSiteMode.None,
            Expires = DateTimeOffset.UtcNow.AddDays(_refreshOptions.Days),
            // Keep tenant auth refresh cookie scoped to tenant auth routes to avoid collisions (esp. in dev).
            Path = "/api/v1/auth"
        };

        if (!string.IsNullOrWhiteSpace(_refreshOptions.CookieDomain))
        {
            var host = Request.Host.Host;
            var domain = _refreshOptions.CookieDomain.Trim();
            var normalizedDomain = domain.TrimStart('.');

            if (host.EndsWith(normalizedDomain, StringComparison.OrdinalIgnoreCase))
            {
                options.Domain = domain;
            }
        }

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
            Path = "/api/v1/auth"
        };

        if (!string.IsNullOrWhiteSpace(_refreshOptions.CookieDomain))
        {
            options.Domain = _refreshOptions.CookieDomain.Trim();
        }

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

    private string GenerateToken(AuthUserResult user, DateTime expiresAtUtc)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.Key));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("accountType", user.AccountType),
            new("scope", user.Scope ?? "tenant")
        };

        if (string.Equals(user.Scope, "tenant", StringComparison.OrdinalIgnoreCase))
        {
            claims.Add(new Claim("tenantId", user.TenantId));
        }

        if (string.Equals(user.AccountType, AccountTypes.Client, StringComparison.OrdinalIgnoreCase))
        {
            claims.Add(new Claim("clientId", user.Id.ToString()));
        }

        if (user.Role.HasValue)
        {
            claims.Add(new Claim(ClaimTypes.Role, user.Role.Value.ToString()));
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

    private static string GenerateCsrfToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var base64 = Convert.ToBase64String(bytes);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
