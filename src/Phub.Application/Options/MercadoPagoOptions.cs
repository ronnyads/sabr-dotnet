namespace Phub.Application.Options;

public sealed class MercadoPagoOptions
{
    public const string SectionName = "MercadoPago";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string AuthBaseUrl { get; set; } = "https://auth.mercadopago.com.br";
    public string ApiBaseUrl { get; set; } = "https://api.mercadopago.com";
    public string TokenUrl { get; set; } = "https://api.mercadopago.com/oauth/token";
    public string BillingApiBaseUrl { get; set; } = "https://api.mercadolibre.com";
    public string RedirectUri { get; set; } = string.Empty;
    public string? ClientPortalBaseUrl { get; set; }
}
