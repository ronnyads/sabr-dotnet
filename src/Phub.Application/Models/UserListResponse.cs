namespace Phub.Application.Models;

public sealed class UserListResponse
{
    public List<UserResult> Items { get; set; } = new();
    public int Total { get; set; }
    public int Skip { get; set; }
    public int Limit { get; set; }
}
