using Sabr.Domain.Enums;

namespace Sabr.Api.Models;

public sealed class ClientDocumentResult
{
    public Guid Id { get; set; }
    public DocumentType DocumentType { get; set; }
    public DocumentStatus Status { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string FileUrl { get; set; } = string.Empty;
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? RequestedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public string? ReviewReason { get; set; }
}
