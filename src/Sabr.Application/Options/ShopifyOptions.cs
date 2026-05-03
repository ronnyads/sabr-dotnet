using System.ComponentModel.DataAnnotations;

namespace Phub.Application.Options;

public sealed class ShopifyOptions
{
    public const string SectionName = "Shopify";

    [Required]
    public string ClientId { get; set; } = string.Empty;

    [Required]
    public string ClientSecret { get; set; } = string.Empty;

    [Required]
    public string RedirectUri { get; set; } = string.Empty;

    // Scopes requested during OAuth. Shopify uses comma-separated scope list.
    public string Scopes { get; set; } = "read_orders,write_orders,read_products,write_products,read_inventory,write_inventory";

    // Optional absolute URL used to redirect back to frontend after OAuth callback.
    // Example (dev): http://localhost:4200
    public string? ClientPortalBaseUrl { get; set; }
}
