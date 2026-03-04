using Microsoft.AspNetCore.Http;
using System.Net;

namespace Sabr.Api.Tenant;

public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ITenantResolver resolver, IWebHostEnvironment env)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (path.StartsWith("/api/v1/client/integrations/mercadolivre/callback", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (path.StartsWith("/api/v1/integrations/mercadolivre/webhook", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Dev convenience: allow admin realm to work on localhost without requiring host=admin.*.
        // In production, platform/admin is always derived from the host (admin.sabr.com.br).
        if (env.IsDevelopment() && IsLocalDevHost(context.Request.Host.Host) &&
            path.StartsWith("/api/v1/admin/", StringComparison.OrdinalIgnoreCase))
        {
            context.Items[TenantContextKeys.IsPlatform] = true;
            await _next(context);
            return;
        }

        var tenantInfo = await resolver.ResolveAsync(context, context.RequestAborted);
        if (tenantInfo == null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "Tenant not found" });
            return;
        }

        context.Items[TenantContextKeys.IsPlatform] = tenantInfo.IsPlatform;
        if (!tenantInfo.IsPlatform)
        {
            context.Items[TenantContextKeys.TenantId] = tenantInfo.Id;
            context.Items[TenantContextKeys.TenantSlug] = tenantInfo.Slug;
        }

        await _next(context);
    }

    private static bool IsLocalDevHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               IPAddress.TryParse(host, out _);
    }
}
