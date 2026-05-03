namespace Phub.Application.Options;

public sealed class TinyErpOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "https://api.tiny.com.br/public-api/v3";
    public string AuthBaseUrl { get; set; } = "https://accounts.tiny.com.br/realms/tiny/protocol/openid-connect";
}
