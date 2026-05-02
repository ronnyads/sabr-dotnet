using System.Security.Claims;

namespace Sabr.Api.Tenant;

public sealed class TenantGuardMiddleware
{
    private readonly RequestDelegate _next;

    public TenantGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!context.User.Identity?.IsAuthenticated ?? true)
        {
            await _next(context);
            return;
        }

        // OAuth callbacks carry tenant context inside the signed state param — not via host header
        if (path.StartsWith("/api/v1/client/integrations/mercadolivre/callback", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/v1/client/integrations/tiktokshop/callback", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var isPlatform = context.Items.TryGetValue(TenantContextKeys.IsPlatform, out var platformObj) &&
                         platformObj is bool p && p;

        if (isPlatform)
        {
            var scope = context.User.FindFirst("scope")?.Value;
            if (!string.Equals(scope, "platform", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "Platform scope required" });
                return;
            }

            await _next(context);
            return;
        }

        if (!context.Items.TryGetValue(TenantContextKeys.TenantId, out var tenantIdObj))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "Tenant not resolved" });
            return;
        }

        var tenantId = tenantIdObj?.ToString();
        var claimTenantId = context.User.FindFirst("tenantId")?.Value;
        var scopeTenant = context.User.FindFirst("scope")?.Value;

        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(claimTenantId) ||
            !string.Equals(scopeTenant, "tenant", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Tenant mismatch" });
            return;
        }

        if (!string.Equals(tenantId, claimTenantId, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Tenant mismatch" });
            return;
        }

        await _next(context);
    }
}
