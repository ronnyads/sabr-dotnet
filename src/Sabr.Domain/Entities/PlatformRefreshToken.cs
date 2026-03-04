namespace Sabr.Domain.Entities;

public sealed class PlatformRefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlatformUserId { get; set; }
    public PlatformUser PlatformUser { get; set; } = null!;

    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }

    public string? CreatedByIp { get; set; }
    public string? CreatedByUserAgent { get; set; }
}
