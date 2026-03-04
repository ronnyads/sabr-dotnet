using System.ComponentModel.DataAnnotations;

namespace Sabr.Application.Options;

public sealed class MercadoLivreOptions
{
    public const string SectionName = "MercadoLivre";

    [Required]
    public string ClientId { get; set; } = string.Empty;

    [Required]
    public string ClientSecret { get; set; } = string.Empty;

    [Required]
    public string AuthBaseUrl { get; set; } = "https://auth.mercadolivre.com.br";

    [Required]
    public string ApiBaseUrl { get; set; } = "https://api.mercadolibre.com";

    [Required]
    public string TokenUrl { get; set; } = "https://api.mercadolibre.com/oauth/token";

    [Required]
    public string RedirectUri { get; set; } = string.Empty;

    // Optional absolute URL used to redirect back to frontend after OAuth callback.
    // Example (dev): http://localhost:4200
    public string? ClientPortalBaseUrl { get; set; }

    [Range(1, 30)]
    public int SyncLookbackDays { get; set; } = 2;

    [Range(1, 30)]
    public int NightlyReconcileLookbackDays { get; set; } = 7;

    [Range(1, 240)]
    public int ReservationTtlHours { get; set; } = 24;

    [Required]
    public string DefaultTimeZoneId { get; set; } = "America/Sao_Paulo";

    [Required]
    public string DefaultCutoffLocalTime { get; set; } = "12:00";

    public string? WebhookSecret { get; set; }

    public MercadoLivreFeatureFlags Features { get; set; } = new();
    public MercadoLivreResilienceOptions Resilience { get; set; } = new();
    public MercadoLivreMabangOptions Mabang { get; set; } = new();
    public Dictionary<string, string> CategoryMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class MercadoLivreFeatureFlags
{
    public bool Publish { get; set; } = false;
    public bool Webhook { get; set; } = false;
    public bool Labels { get; set; } = false;
    public bool Shipments { get; set; } = false;
    public bool Mabang { get; set; } = false;
    public bool Reconcile { get; set; } = false;
    public bool SlaByMode { get; set; } = true;
}

public sealed class MercadoLivreResilienceOptions
{
    [Range(1, 10)]
    public int RetryMaxAttempts { get; set; } = 3;

    [Range(50, 10000)]
    public int RetryBaseDelayMs { get; set; } = 300;

    [Range(1, 50)]
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    [Range(1, 600)]
    public int CircuitBreakerDurationSeconds { get; set; } = 30;
}

public sealed class MercadoLivreMabangOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8080";
    public string LabelEndpoint { get; set; } = "/api/v1/labels";
    public string ApiKeyHeader { get; set; } = "X-Api-Key";
    public string? ApiKey { get; set; }

    [Range(1, 120)]
    public int TimeoutSeconds { get; set; } = 15;
}
