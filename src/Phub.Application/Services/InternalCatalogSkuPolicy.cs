namespace Phub.Application.Services;

public static class InternalCatalogSkuPolicy
{
    public static bool IsMarketplaceExternalIdentifier(string? sku)
    {
        if (string.IsNullOrWhiteSpace(sku)) return false;
        var normalized = sku.Trim().ToUpperInvariant();
        if (!normalized.StartsWith("MLB", StringComparison.Ordinal)) return false;
        var identifier = normalized.StartsWith("MLBU", StringComparison.Ordinal)
            ? normalized[4..]
            : normalized[3..];
        return identifier.Length > 0 && identifier.All(char.IsDigit);
    }
}
