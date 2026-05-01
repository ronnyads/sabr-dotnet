using System.ComponentModel.DataAnnotations;

namespace Sabr.Application.Options;

public sealed class TikTokShopOptions
{
    public const string SectionName = "TikTokShop";

    [Required]
    public string AppKey { get; set; } = string.Empty;

    [Required]
    public string AppSecret { get; set; } = string.Empty;

    [Required]
    public string RedirectUri { get; set; } = string.Empty;

    public string AuthBaseUrl { get; set; } = "https://auth.tiktok-shops.com";

    public string AuthorizePath { get; set; } = "/oauth/authorize";

    public string ApiBaseUrl { get; set; } = "https://open-api.tiktokglobalshop.com";

    public string TokenPath { get; set; } = "/api/token/getAccessToken";

    public string RefreshTokenPath { get; set; } = "/api/token/refreshToken";

    public string Scopes { get; set; } = "seller.base,product.read,product.write,order.read";

    public string? ClientPortalBaseUrl { get; set; }

    public TikTokShopFeatureFlags Features { get; set; } = new();
}

public sealed class TikTokShopFeatureFlags
{
    public bool Webhook { get; set; } = false;
    public bool Sync { get; set; } = true;
    public bool Publish { get; set; } = false;
}
