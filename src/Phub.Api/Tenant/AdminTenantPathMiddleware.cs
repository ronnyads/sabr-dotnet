using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;

namespace Phub.Api.Tenant;

/// <summary>
/// For admin routes that carry tenantId in the path, populate TenantId in context while keeping IsPlatform=true.
/// </summary>
public sealed class AdminTenantPathMiddleware
{
    private static readonly Regex GuidRegex = new("^[0-9a-fA-F-]{36}$", RegexOptions.Compiled);
    private readonly RequestDelegate _next;

    public AdminTenantPathMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/api/v1/admin/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Keep platform mode
        context.Items[TenantContextKeys.IsPlatform] = true;

        var routeValues = context.GetRouteData()?.Values;
        if (routeValues != null && routeValues.TryGetValue("tenantId", out var tenantIdObj))
        {
            var tenantId = tenantIdObj?.ToString();
            if (!string.IsNullOrWhiteSpace(tenantId) && GuidRegex.IsMatch(tenantId))
            {
                context.Items[TenantContextKeys.TenantId] = tenantId;
            }
        }

        await _next(context);
    }
}
