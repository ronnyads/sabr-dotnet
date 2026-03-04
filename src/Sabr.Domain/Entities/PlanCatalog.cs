namespace Sabr.Domain.Entities;

public sealed class PlanCatalog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid PlanId { get; set; }
    public Guid CatalogId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
