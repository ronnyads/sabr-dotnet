using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Phub.Api.Models;
using Phub.Application.Models;
using Phub.Application.Options;
using Phub.Application.Services;

namespace Phub.Api.Controllers;

[ApiController]
[Route("api/v1/supplier/auth")]
public sealed class SupplierAuthController : ControllerBase
{
    private const string SupplierCookieName = "phub.supplier.rt";
    private const string SupplierCookiePath = "/api/v1/supplier/auth";

    private readonly SupplierAuthService _supplierAuthService;
    private readonly JwtOptions _jwtOptions;
    private readonly RefreshTokenOptions _refreshOptions;

    public SupplierAuthController(
        SupplierAuthService supplierAuthService,
        IOptions<JwtOptions> jwtOptions,
        IOptions<RefreshTokenOptions> refreshOptions)
    {
        _supplierAuthService = supplierAuthService;
        _jwtOptions = jwtOptions.Value;
        _refreshOptions = refreshOptions.Value;
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] AuthLoginRequest request, CancellationToken cancellationToken)
    {
        var result = await _supplierAuthService.LoginAsync(request, cancellationToken);
        if (!result.Succeeded || result.Data == null)
            return Unauthorized(new { errors = result.Errors });

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_jwtOptions.AccessTokenMinutes);
        var token = GenerateToken(result.Data, expiresAt.UtcDateTime);
        var refreshToken = await _supplierAuthService.IssueRefreshTokenAsync(
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
            AccountType = "supplier",
            User = result.Data,
            RefreshToken = refreshToken
        });
    }

    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] SupplierRegisterRequest request, CancellationToken cancellationToken)
    {
        var result = await _supplierAuthService.RegisterAsync(request, cancellationToken);
        if (!result.Succeeded || result.Data == null)
            return BadRequest(new { errors = result.Errors });

        return Ok(new { message = "Registration submitted. Awaiting admin approval.", supplier = result.Data });
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest? request, CancellationToken cancellationToken)
    {
        var refreshToken =
            Request.Cookies[SupplierCookieName] ??
            request?.RefreshToken ??
            Request.Headers["X-Refresh-Token"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(refreshToken))
            return Unauthorized(new { error = "Refresh token missing" });

        var result = await _supplierAuthService.RefreshAsync(
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
        var token = GenerateToken(result.Data.User, expiresAt.UtcDateTime);
        SetRefreshCookie(result.Data.RefreshToken);

        return Ok(new AuthLoginResponse
        {
            AccessToken = token,
            ExpiresAt = expiresAt,
            AccountType = "supplier",
            User = result.Data.User
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
            options.Domain = _refreshOptions.CookieDomain.Trim();

        Response.Cookies.Append("phub.supplier.csrf", token, options);
        return Ok(new { token, header = "X-Supplier-CSRF-Token" });
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var refreshToken = Request.Cookies[SupplierCookieName];
        if (!string.IsNullOrWhiteSpace(refreshToken))
            await _supplierAuthService.RevokeRefreshTokenAsync(refreshToken, cancellationToken);

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
            Path = SupplierCookiePath
        };

        if (!string.IsNullOrWhiteSpace(_refreshOptions.CookieDomain))
        {
            var host = Request.Host.Host;
            var domain = _refreshOptions.CookieDomain.Trim();
            var normalizedDomain = domain.TrimStart('.');
            if (host.EndsWith(normalizedDomain, StringComparison.OrdinalIgnoreCase))
                options.Domain = domain;
        }

        Response.Cookies.Append(SupplierCookieName, token, options);
    }

    private void ClearRefreshCookie()
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = _refreshOptions.RequireHttps,
            SameSite = SameSiteMode.None,
            Expires = DateTimeOffset.UtcNow.AddDays(-1),
            Path = SupplierCookiePath
        };

        if (!string.IsNullOrWhiteSpace(_refreshOptions.CookieDomain))
            options.Domain = _refreshOptions.CookieDomain.Trim();

        Response.Cookies.Append(SupplierCookieName, string.Empty, options);
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
                    return parts[0].Trim();
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
            new("accountType", "supplier"),
            new("scope", "supplier"),
            new("supplierId", user.Id.ToString())
        };

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
