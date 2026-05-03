namespace Phub.Api.Tenant;

public sealed class SupplierGuardMiddleware
{
    private readonly RequestDelegate _next;

    public SupplierGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (!path.StartsWith("/api/v1/supplier/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Auth endpoints are anonymous
        if (path.StartsWith("/api/v1/supplier/auth/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!context.User.Identity?.IsAuthenticated ?? true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Authentication required" });
            return;
        }

        var scope = context.User.FindFirst("scope")?.Value;
        if (!string.Equals(scope, "supplier", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Supplier scope required" });
            return;
        }

        await _next(context);
    }
}
