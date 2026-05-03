using Microsoft.AspNetCore.Http;
using System.Net;

namespace Phub.Api.Tenant;

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
        var method = context.Request.Method;
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
            if (CanProceedWithoutResolvedTenant(path, method))
            {
                await _next(context);
                return;
            }

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

    private static bool CanProceedWithoutResolvedTenant(string path, string method)
    {
        return (HttpMethods.Post.Equals(method, StringComparison.OrdinalIgnoreCase) &&
                path.StartsWith("/api/v1/auth/login", StringComparison.OrdinalIgnoreCase)) ||
               (HttpMethods.Post.Equals(method, StringComparison.OrdinalIgnoreCase) &&
                path.StartsWith("/api/v1/auth/bootstrap", StringComparison.OrdinalIgnoreCase)) ||
               (HttpMethods.Get.Equals(method, StringComparison.OrdinalIgnoreCase) &&
                path.StartsWith("/api/v1/auth/csrf", StringComparison.OrdinalIgnoreCase));
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
