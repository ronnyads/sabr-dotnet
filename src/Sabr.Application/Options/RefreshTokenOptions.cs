using System.ComponentModel.DataAnnotations;

namespace Sabr.Application.Options;

public sealed class RefreshTokenOptions
{
    [Range(1, 365)]
    public int Days { get; set; } = 30;

    [Required]
    public string CookieName { get; set; } = "sabr_rt";

    public string? CookieDomain { get; set; }

    public bool RequireHttps { get; set; } = true;
}
