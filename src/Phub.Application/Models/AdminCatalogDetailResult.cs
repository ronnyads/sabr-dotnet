namespace Phub.Application.Models;

public sealed class AdminCatalogDetailResult
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public List<string> ProductSkus { get; set; } = new();
    public List<Guid> PlanIds { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
