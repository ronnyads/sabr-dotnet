namespace Sabr.Api.Models;

public sealed class ClientDocumentReviewRequest
{
    public Guid? ReviewedByUserId { get; set; }
    public string? Reason { get; set; }
}
