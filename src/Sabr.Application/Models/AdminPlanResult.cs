using Sabr.Domain.Enums;
using System.Text.Json.Serialization;

namespace Sabr.Application.Models;

public sealed class AdminPlanResult
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BillingPeriod BillingPeriod { get; set; }
    public bool IsActive { get; set; }
    public int CatalogCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
