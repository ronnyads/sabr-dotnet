using Phub.Domain.Enums;

namespace Phub.Application.Models;

public sealed class AdminCatalogUpsertRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public CatalogAccessMode AccessMode { get; set; } = CatalogAccessMode.Public;
    public bool IsActive { get; set; } = true;
}
