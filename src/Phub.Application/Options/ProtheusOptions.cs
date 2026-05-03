using System.ComponentModel.DataAnnotations;

namespace Phub.Application.Options;

public sealed class ProtheusOptions
{
    [Required, Url]
    public string BaseUrl { get; set; } = string.Empty;

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 30;
}
