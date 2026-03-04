namespace Sabr.Api.Models;

public sealed class ClientDocumentListResponse
{
    public List<ClientDocumentResult> Items { get; set; } = new();
    public int Total { get; set; }
    public int Skip { get; set; }
    public int Limit { get; set; }
}
