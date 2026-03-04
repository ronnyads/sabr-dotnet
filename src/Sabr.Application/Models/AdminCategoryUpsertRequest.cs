namespace Sabr.Application.Models;

public sealed class AdminCategoryUpsertRequest
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public Guid? ParentId { get; set; }
    public string? Icon { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}
