namespace Sabr.Application.Models;

public sealed class CatalogReplacePlansRequest
{
    public List<Guid> PlanIds { get; set; } = new();
}
