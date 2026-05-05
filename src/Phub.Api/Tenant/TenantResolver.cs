using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Phub.Domain.Enums;
using Phub.Infrastructure.Persistence;

namespace Phub.Api.Tenant;

public sealed class TenantResolver : ITenantResolver
{
    private static readonly HashSet<string> ReservedSubdomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "api",
        "www",
        "admin",
        "platform",
        "app"
    };
    private readonly AppDbContext _dbContext;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<TenantResolver> _logger;
    private static readonly Regex SlugRegex = new("^[a-z0-9-]{3,32}$", RegexOptions.Compiled);

    public TenantResolver(AppDbContext dbContext, IWebHostEnvironment environment, ILogger<TenantResolver> logger)
    {
        _dbContext = dbContext;
        _environment = environment;
        _logger = logger;
    }

    public async Task<TenantInfo?> ResolveAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var host = context.Request.Host.Host;
        var slug = ExtractSlug(host, context);

        // API hostname (api.*) não carrega slug; tente origin ou header mesmo em produção.
        if (string.IsNullOrWhiteSpace(slug) || string.Equals(slug, "api", StringComparison.OrdinalIgnoreCase))
        {
            var originSlug = ExtractSlugFromOrigin(context);
            if (string.IsNullOrWhiteSpace(originSlug) || ReservedSubdomains.Contains(originSlug))
            {
                slug = ExtractSlugFromHeader(context);
                _logger.LogInformation("[TenantResolver] Using header slug (origin was empty or reserved): {HeaderSlug}", slug ?? "(null)");
            }
            else
            {
                slug = originSlug;
            }
        }

        // Dev-only fallback: when running API on localhost/IP but the SPA is on a subdomain (lvh.me),
        // allow extracting slug from Origin to avoid having to pass X-Tenant everywhere.
        if (string.IsNullOrWhiteSpace(slug) && _environment.IsDevelopment() && IsLocalDevHost(host))
        {
            slug = ExtractSlugFromOrigin(context) ?? ExtractSlugFromHeader(context);
        }
        if (string.IsNullOrWhiteSpace(slug))
        {
            _logger.LogWarning("[TenantResolver] No slug found in host, origin, or header.");
            return null;
        }

        _logger.LogInformation("[TenantResolver] Resolving tenant with slug: {Slug}", slug);

        if (string.Equals(slug, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return new TenantInfo(string.Empty, string.Empty, true);
        }

        var normalizedSlug = slug.Trim().ToLowerInvariant();

        if (!SlugRegex.IsMatch(normalizedSlug))
        {
            _logger.LogWarning("[TenantResolver] Slug '{NormalizedSlug}' does not match regex pattern.", normalizedSlug);
            return null;
        }

        if (ReservedSubdomains.Contains(normalizedSlug))
        {
            _logger.LogWarning("[TenantResolver] Slug '{NormalizedSlug}' is a reserved subdomain.", normalizedSlug);
            return null;
        }
        var tenant = await _dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == normalizedSlug && t.Status == TenantStatus.Active, cancellationToken);

        if (tenant == null)
        {
            _logger.LogWarning("[TenantResolver] Slug '{Slug}' not found with Active status.", normalizedSlug);

            // Debug: log all tenants with this slug regardless of status
            var allWithSlug = await _dbContext.Tenants
                .AsNoTracking()
                .Where(t => t.Slug == normalizedSlug)
                .ToListAsync(cancellationToken);
            _logger.LogInformation("[TenantResolver] Total tenants with slug '{Slug}': {Count}", normalizedSlug, allWithSlug.Count);
            foreach (var t in allWithSlug)
            {
                _logger.LogInformation("[TenantResolver] Found tenant - Id: {TenantId}, Slug: {TenantSlug}, Status: {TenantStatus}", t.Id, t.Slug, t.Status);
            }
            return null;
        }

        _logger.LogInformation("[TenantResolver] Successfully resolved tenant: {TenantId} with slug: {TenantSlug}", tenant.Id, tenant.Slug);
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

    private static string? ExtractSlugFromHeader(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Tenant", out var headerTenant))
        {
            var value = headerTenant.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
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
