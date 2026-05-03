namespace Phub.Application.Models;

public sealed class AdminCategoryTreeNodeResult
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public Guid? ParentId { get; set; }
    public bool IsActive { get; set; }
    public string Path { get; set; } = string.Empty;
    public IReadOnlyCollection<AdminCategoryTreeNodeResult> Children { get; set; } = Array.Empty<AdminCategoryTreeNodeResult>();
}
