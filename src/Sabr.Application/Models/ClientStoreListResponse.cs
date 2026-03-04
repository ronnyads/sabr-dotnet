namespace Sabr.Application.Models;

public sealed class ClientStoreListResponse
{
    public List<ClientStoreResult> Items { get; set; } = new();
    public int Total { get; set; }
}
