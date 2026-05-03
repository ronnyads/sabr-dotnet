using Phub.Domain.Protheus;

namespace Phub.Domain.Common;

public abstract class EntityBase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;

    public string ProtheusTag { get; set; } = string.Empty;
    public ProtheusOperationType ProtheusOperation { get; set; } = ProtheusOperationType.CREATE;
    public string? ProtheusRef { get; set; }
    public DateTimeOffset? ProtheusLastSyncAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
