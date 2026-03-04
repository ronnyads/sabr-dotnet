using Sabr.Domain.Enums;

namespace Sabr.Application.Models;

public sealed class ClientApprovalResult
{
    public Guid ClientId { get; set; }
    public ClientStatus Status { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
    public string? RejectionReason { get; set; }
}
