using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

public sealed class Catalog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public CatalogAccessMode AccessMode { get; set; } = CatalogAccessMode.PlanRestricted;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
