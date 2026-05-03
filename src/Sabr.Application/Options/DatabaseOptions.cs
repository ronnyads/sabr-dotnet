using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Phub.Application.Options;

public sealed class DatabaseOptions
{
    [Required]
    public string Host { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; set; } = 5432;

    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string User { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    public bool Pooling { get; set; } = true;
    public int? MinPoolSize { get; set; }
    public int? MaxPoolSize { get; set; }
    public string? SslMode { get; set; }
    public string? Schema { get; set; }

    public string BuildConnectionString()
    {
        var parts = new List<string>
        {
            $"Host={Host}",
            $"Port={Port}",
            $"Database={Name}",
            $"Username={User}",
            $"Password={Password}"
        };

        if (Pooling)
        {
            parts.Add("Pooling=true");
            if (MinPoolSize.HasValue) parts.Add($"Minimum Pool Size={MinPoolSize.Value}");
            if (MaxPoolSize.HasValue) parts.Add($"Maximum Pool Size={MaxPoolSize.Value}");
        }

        if (!string.IsNullOrWhiteSpace(SslMode))
        {
            parts.Add($"SslMode={SslMode}");
        }

        if (!string.IsNullOrWhiteSpace(Schema))
        {
            parts.Add($"Search Path={Schema}");
        }

        return string.Join(";", parts);
    }
}
