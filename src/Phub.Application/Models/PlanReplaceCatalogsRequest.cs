namespace Phub.Application.Models;

public sealed class PlanReplaceCatalogsRequest
{
    public List<Guid> CatalogIds { get; set; } = new();
}
