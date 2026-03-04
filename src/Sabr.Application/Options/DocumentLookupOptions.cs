namespace Sabr.Application.Options;

public sealed class DocumentLookupOptions
{
    public string Mode { get; set; } = "PublicCnpj"; // PublicCnpj | Mock
}

public sealed class PublicCnpjOptions
{
    public string BaseUrl { get; set; } = "https://brasilapi.com.br/";
    public int TimeoutSeconds { get; set; } = 5;
    public int CacheMinutes { get; set; } = 30;
    public string Provider { get; set; } = "brasilapi"; // hoje apenas brasilapi
}
