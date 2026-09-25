namespace Phub.Domain.Entities;

public static class CatalogCostStatuses
{
    public const string Resolved = "RESOLVED";
    public const string Pending = "CATALOG_COST_PENDING";
}

public static class CatalogPriceOrigins
{
    public const string None = "NONE";
    public const string MasterProduct = "MASTER_PRODUCT";
    public const string VariantOverride = "VARIANT_OVERRIDE";
    public const string ExternalSupplier = "EXTERNAL_SUPPLIER";
    public const string PrePurchasedLot = "PREPURCHASED_LOT";

    public const string MarketplaceListing = "MARKETPLACE_LISTING";
    public const string OrderPrice = "ORDER_PRICE";
    public const string ChannelPrice = "CHANNEL_PRICE";

    public static bool IsAuthorizedCatalogOrigin(string? origin) => origin is
        MasterProduct or VariantOverride or ExternalSupplier or PrePurchasedLot;

    public static bool IsChannelPriceOrigin(string? origin) => origin is
        MarketplaceListing or OrderPrice or ChannelPrice;
}

/// <summary>
/// Domain invariant for the internal cost charged to the seller. A marketplace sale/listing
/// price is channel revenue and can never become an internal catalog cost.
/// </summary>
public static class CatalogCostPolicy
{
    public static void EnsureValid(long catalogPriceCents, string costStatus, string priceOrigin)
    {
        if (CatalogPriceOrigins.IsChannelPriceOrigin(priceOrigin))
            throw new InvalidOperationException("Marketplace listing, order or channel price cannot be used as CatalogPrice.");

        if (string.Equals(costStatus, CatalogCostStatuses.Pending, StringComparison.Ordinal))
        {
            if (catalogPriceCents != 0)
                throw new InvalidOperationException("A product with pending catalog cost cannot carry a catalog price.");
            if (!string.Equals(priceOrigin, CatalogPriceOrigins.None, StringComparison.Ordinal))
                throw new InvalidOperationException("A product with pending catalog cost must not claim a price origin.");
            return;
        }

        if (!string.Equals(costStatus, CatalogCostStatuses.Resolved, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported catalog cost status '{costStatus}'.");
        if (catalogPriceCents <= 0)
            throw new InvalidOperationException("A resolved catalog cost must be greater than zero.");
        if (!CatalogPriceOrigins.IsAuthorizedCatalogOrigin(priceOrigin))
            throw new InvalidOperationException($"Catalog price origin '{priceOrigin}' is not authorized.");
    }
}
