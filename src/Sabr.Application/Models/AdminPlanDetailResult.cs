using Phub.Domain.Enums;
using System.Text.Json.Serialization;

namespace Phub.Application.Models;

public sealed class AdminPlanDetailResult
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BillingPeriod BillingPeriod { get; set; }
    public bool IsActive { get; set; }
    public List<Guid> CatalogIds { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
