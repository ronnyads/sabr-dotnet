namespace Phub.Api.Tenant;

public sealed record TenantInfo(string Id, string Slug, bool IsPlatform = false);
