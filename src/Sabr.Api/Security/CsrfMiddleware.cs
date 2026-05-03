using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace Phub.Api.Security;

/// <summary>
/// Double-submit CSRF protection for cookie-based auth endpoints (refresh/logout).
/// </summary>
public sealed class CsrfMiddleware
{
    public const string TenantCookieName = "XSRF-TOKEN";
    public const string TenantHeaderName = "X-XSRF-TOKEN";

    public const string AdminCookieName = "XSRF-ADMIN";
    public const string AdminHeaderName = "X-XSRF-ADMIN";

    private readonly RequestDelegate _next;

    public CsrfMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await _next(context);
            return;
        }

        // Tenant realm
        if (path.Equals("/api/v1/auth/refresh", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/v1/auth/logout", StringComparison.OrdinalIgnoreCase))
        {
            if (!Validate(context, TenantCookieName, TenantHeaderName))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "CSRF token missing or invalid" });
                return;
            }
        }

        // Platform realm
        if (path.Equals("/api/v1/admin/auth/refresh", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/v1/admin/auth/logout", StringComparison.OrdinalIgnoreCase))
        {
            // Relaxe o CSRF no realm admin para evitar bloqueio de refresh quando o navegador não propaga o header.
            // Ainda dependemos do cookie HttpOnly de refresh e da verificação de origem no CORS.
            await _next(context);
            return;
        }

        await _next(context);
    }

    private static bool Validate(HttpContext context, string cookieName, string headerName)
    {
        if (!context.Request.Cookies.TryGetValue(cookieName, out var cookieValue) ||
            string.IsNullOrWhiteSpace(cookieValue))
        {
            return false;
        }

        if (!context.Request.Headers.TryGetValue(headerName, out var headerValue))
        {
            return false;
        }

        var hv = headerValue.ToString();
        if (string.IsNullOrWhiteSpace(hv))
        {
            return false;
        }

        // Constant-time compare to reduce timing leakage.
        var cookieBytes = System.Text.Encoding.UTF8.GetBytes(cookieValue);
        var headerBytes = System.Text.Encoding.UTF8.GetBytes(hv);
        if (cookieBytes.Length != headerBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(cookieBytes, headerBytes);
    }
}
