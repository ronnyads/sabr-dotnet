using System.Text.Json.Serialization;

namespace Phub.Application.Models;

public sealed class AdminProductUpdateRequest
{
    public string? Name { get; set; }
    public string? Brand { get; set; }
    public string? Ncm { get; set; }
    public string? Ean { get; set; }
    public string? Description { get; set; }
    public string? CategoryId { get; set; }
    public string? ThumbnailUrl { get; set; }
    public decimal? WidthCm { get; set; }
    public decimal? HeightCm { get; set; }
    public decimal? LengthCm { get; set; }
    public decimal? WeightKg { get; set; }
    public bool? RequiresAnatel { get; set; }
    public string? AnatelHomologationNumber { get; set; }
    public Guid? AnatelDocumentId { get; set; }
    public string? TenantSlug { get; set; }
    public long? CostPriceCents { get; set; }
    public long? CatalogPriceCents { get; set; }
    public bool? IsActive { get; set; }
    public string? Reason { get; set; }

    [JsonIgnore]
    public bool CategoryIdProvided { get; set; }
}
