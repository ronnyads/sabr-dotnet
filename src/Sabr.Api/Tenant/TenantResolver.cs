using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Sabr.Domain.Enums;
using Sabr.Infrastructure.Persistence;

namespace Sabr.Api.Tenant;

public sealed class TenantResolver : ITenantResolver
{
    private static readonly HashSet<string> ReservedSubdomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "api",
        "www",
        "admin",
        "platform"
    };
    private readonly AppDbContext _dbContext;
    private readonly IWebHostEnvironment _environment;
    private static readonly Regex SlugRegex = new("^[a-z0-9-]{3,32}$", RegexOptions.Compiled);

    public TenantResolver(AppDbContext dbContext, IWebHostEnvironment environment)
    {
        _dbContext = dbContext;
        _environment = environment;
    }

    public async Task<TenantInfo?> ResolveAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var host = context.Request.Host.Host;
        var slug = ExtractSlug(host, context);

        // Dev-only fallback: when running API on localhost/IP but the SPA is on a subdomain (lvh.me),
        // allow extracting slug from Origin to avoid having to pass X-Tenant everywhere.
        if (string.IsNullOrWhiteSpace(slug) && _environment.IsDevelopment() && IsLocalDevHost(host))
        {
            slug = ExtractSlugFromOrigin(context);
        }
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        if (string.Equals(slug, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return new TenantInfo(string.Empty, string.Empty, true);
        }

        var normalizedSlug = slug.Trim().ToLowerInvariant();
        if (!SlugRegex.IsMatch(normalizedSlug) || ReservedSubdomains.Contains(normalizedSlug))
        {
            return null;
        }

        var tenant = await _dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == normalizedSlug && t.Status == TenantStatus.Active, cancellationToken);

        if (tenant == null)
        {
            return null;
        }

        return new TenantInfo(tenant.Id, tenant.Slug, false);
    }

    private string? ExtractSlug(string host, HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        if (host.Contains('.'))
        {
            var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var subdomain = parts[0];
                if (string.Equals(parts[^1], "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    return subdomain;
                }

                if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) && !IsIpAddress(host))
                {
                    return subdomain;
                }
            }
        }

        if (_environment.IsDevelopment() && IsLocalDevHost(host))
        {
            if (context.Request.Headers.TryGetValue("X-Tenant", out var headerTenant))
            {
                var value = headerTenant.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            if (context.Request.Query.TryGetValue("tenant", out var queryTenant))
            {
                return queryTenant.ToString();
            }
        }

        return null;
    }

    private static string? ExtractSlugFromOrigin(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("Origin", out var originValue))
        {
            return null;
        }

        if (!Uri.TryCreate(originValue.ToString(), UriKind.Absolute, out var origin))
        {
            return null;
        }

        var originHost = origin.Host;
        if (string.IsNullOrWhiteSpace(originHost))
        {
            return null;
        }

        var parts = originHost.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        return parts[0];
    }

    private static bool IsIpAddress(string host)
    {
        return IPAddress.TryParse(host, out _);
    }

    private static bool IsLocalDevHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || IsIpAddress(host);
    }
}
