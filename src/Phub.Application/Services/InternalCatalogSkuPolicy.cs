namespace Phub.Application.Services;

public static class InternalCatalogSkuPolicy
{
    public static bool IsMarketplaceExternalIdentifier(string? sku)
    {
        if (string.IsNullOrWhiteSpace(sku)) return false;
        var normalized = sku.Trim().ToUpperInvariant();
        return normalized.Length > 3
               && normalized.StartsWith("MLB", StringComparison.Ordinal)
               && normalized[3..].All(char.IsDigit);
    }
}
