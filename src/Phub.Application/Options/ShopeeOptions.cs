using System.ComponentModel.DataAnnotations;

namespace Phub.Application.Options;

public sealed class ShopeeOptions
{
    public const string SectionName = "Shopee";

    [Required]
    public string PartnerId { get; set; } = string.Empty;

    [Required]
    public string PartnerKey { get; set; } = string.Empty;

    [Required]
    public string RedirectUri { get; set; } = string.Empty;

    public string AuthorizationBaseUrl { get; set; } = "https://open.shopee.com.br";

    public string AuthorizationPath { get; set; } = "/auth";

    public string ApiBaseUrl { get; set; } = "https://partner.shopeemobile.com";

    public string AuthType { get; set; } = "seller";

    public string? ClientPortalBaseUrl { get; set; }

    [Range(1, 15)]
    public int SyncLookbackDays { get; set; } = 2;

    public ShopeeFeatureFlags Features { get; set; } = new();
}

public sealed class ShopeeFeatureFlags
{
    public bool Sync { get; set; } = true;
    public bool Publish { get; set; } = false;
}
