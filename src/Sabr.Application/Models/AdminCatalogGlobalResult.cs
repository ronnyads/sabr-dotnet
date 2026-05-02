namespace Sabr.Application.Models;

/// <summary>
/// Resultado de catálogo para view global (SuperAdmin).
/// Catálogos são recursos de plataforma — não pertencem a um tenant específico.
/// </summary>
public sealed class AdminCatalogGlobalResult
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public int ProductCount { get; set; }
    public int PlanCount { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
