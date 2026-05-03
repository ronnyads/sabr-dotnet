using Phub.Domain.Enums;
using System.Text.Json.Serialization;

namespace Phub.Application.Models;

public sealed class ClientPlanSubscriptionItemResult
{
    public Guid PlanId { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BillingPeriod BillingPeriod { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
}
