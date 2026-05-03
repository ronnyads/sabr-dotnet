using System.ComponentModel.DataAnnotations;

namespace Phub.Application.Options;

public sealed class BootstrapOptions
{
    public bool Enabled { get; set; } = true;

    [Required]
    public string AdminKey { get; set; } = string.Empty;
}
