namespace Phub.Application.Models;

public sealed class AdminCatalogUpsertRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}
