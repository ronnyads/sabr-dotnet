namespace Sabr.Application.Models;

public sealed class ClientStoreResult
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;
    public string? Name { get; set; }
    public bool IsActive { get; set; }
}
