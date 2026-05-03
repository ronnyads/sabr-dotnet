namespace Phub.Api.Tenant;

public interface ITenantResolver
{
    Task<TenantInfo?> ResolveAsync(HttpContext context, CancellationToken cancellationToken = default);
}
