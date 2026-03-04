using System.ComponentModel.DataAnnotations;

namespace Sabr.Application.Options;

public sealed class JwtOptions
{
    [Required]
    public string Issuer { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;

    [Range(5, 1440)]
    public int AccessTokenMinutes { get; set; } = 120;
}
