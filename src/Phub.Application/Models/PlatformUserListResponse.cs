namespace Phub.Application.Models;

public sealed class PlatformUserListResponse
{
    public IReadOnlyCollection<PlatformUserResult> Items { get; set; } = Array.Empty<PlatformUserResult>();
    public int Total { get; set; }
    public int Skip { get; set; }
    public int Limit { get; set; }
}
