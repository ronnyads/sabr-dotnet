using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

public sealed class MarketplaceOperationJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public MarketplaceProvider Provider { get; set; } = MarketplaceProvider.MercadoLivre;
    public string OperationType { get; set; } = string.Empty;
    public string? DedupeKey { get; set; }
    public string Status { get; set; } = "PENDING";
    public string PayloadJson { get; set; } = "{}";
    public string ResultJson { get; set; } = "{}";
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Attempts { get; set; }
    public long? InventoryVersion { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
