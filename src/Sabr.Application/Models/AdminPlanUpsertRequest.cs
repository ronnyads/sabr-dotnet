using Sabr.Domain.Enums;
using System.Text.Json.Serialization;

namespace Sabr.Application.Models;

public sealed class AdminPlanUpsertRequest
{
    public string Name { get; set; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BillingPeriod? BillingPeriod { get; set; }
    public bool IsActive { get; set; } = true;
}
