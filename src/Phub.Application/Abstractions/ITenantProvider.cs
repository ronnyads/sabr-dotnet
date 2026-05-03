namespace Phub.Application.Abstractions;

public interface ITenantProvider
{
    string? TenantId { get; }
    string? TenantSlug { get; }
    bool IsPlatform { get; }
}
