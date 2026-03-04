namespace Sabr.Application.Models;

public sealed class ClientPlanSubscriptionsResult
{
    public Guid ClientId { get; set; }
    public string TenantSlug { get; set; } = string.Empty;
    public List<ClientPlanSubscriptionItemResult> Items { get; set; } = new();
}
