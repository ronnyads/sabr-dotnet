using Phub.Application.Abstractions;

namespace Phub.Api.Tenant;

public sealed class HttpContextTenantProvider : ITenantProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextTenantProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string? TenantId
    {
        get
        {
            var context = _httpContextAccessor.HttpContext;
            if (context == null) return null;

            if (context.Items.TryGetValue(TenantContextKeys.TenantId, out var value))
            {
                return value?.ToString();
            }

            return null;
        }
    }

    public string? TenantSlug
    {
        get
        {
            var context = _httpContextAccessor.HttpContext;
            if (context == null) return null;

            if (context.Items.TryGetValue(TenantContextKeys.TenantSlug, out var value))
            {
                return value?.ToString();
            }

            return null;
        }
    }

    public bool IsPlatform
    {
        get
        {
            var context = _httpContextAccessor.HttpContext;
            if (context == null) return false;

            if (context.Items.TryGetValue(TenantContextKeys.IsPlatform, out var value) && value is bool b)
            {
                return b;
            }

            return false;
        }
    }
}
