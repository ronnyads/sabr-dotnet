using Sabr.Domain.Common;
using Sabr.Domain.Enums;

namespace Sabr.Domain.Entities;

public sealed class ClientDocument : EntityBase
{
    public Guid ClientId { get; set; }
    public Client Client { get; set; } = null!;

    public DocumentType DocumentType { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string FileUrl { get; set; } = string.Empty;

    public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RequestedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public string? ReviewReason { get; set; }
}
