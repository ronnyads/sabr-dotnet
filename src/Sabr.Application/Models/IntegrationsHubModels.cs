namespace Sabr.Application.Models;

public sealed class IntegrationCardResult
{
    public int Provider { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int ConnectedCount { get; set; }
}

public sealed class ClientIntegrationCardResult
{
    public int Provider { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsConnected { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public string? Details { get; set; }
}

public sealed class IntegrationClientResult
{
    public Guid ClientId { get; set; }
    public string TenantId { get; set; } = "";
    public string TenantSlug { get; set; } = "";
    public string ClientName { get; set; } = "";
    public bool IsConnected { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public string? SellerOrCompanyInfo { get; set; }
}

public sealed class PagedIntegrationClientsResult
{
    public List<IntegrationClientResult> Items { get; set; } = new();
    public int Total { get; set; }
}
