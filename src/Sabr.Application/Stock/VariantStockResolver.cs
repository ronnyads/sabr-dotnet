using Sabr.Domain.Entities;
using Sabr.Domain.ValueObjects;

namespace Sabr.Application.Stock;

public enum StockSource
{
    ExactVariant,
    AutoBestVariant,
    FallbackZero
}

public sealed record ResolvedVariantStock(
    string? ResolvedVariantSku,
    int AvailableStock,
    StockSource StockSource);

public static class VariantStockResolver
{
    public static ResolvedVariantStock Resolve(
        string? requestedVariantSku,
        IReadOnlyCollection<ProductVariant> variants)
    {
        var active = variants
            .Where(item => item.IsActive)
            .ToList();

        if (!string.IsNullOrWhiteSpace(requestedVariantSku))
        {
            var normalizedRequestedSku = Sku.Normalize(requestedVariantSku);
            var exact = active.FirstOrDefault(item => string.Equals(item.VariantSku, normalizedRequestedSku, StringComparison.Ordinal));
            if (exact != null)
            {
                return new ResolvedVariantStock(
                    exact.VariantSku,
                    SafeStock(exact.AvailableStock),
                    StockSource.ExactVariant);
            }
        }

        if (active.Count > 0)
        {
            var best = active
                .OrderByDescending(item => SafeStock(item.AvailableStock))
                .ThenByDescending(item => SafeStock(item.PhysicalStock))
                .ThenBy(item => item.VariantSku, StringComparer.OrdinalIgnoreCase)
                .First();

            return new ResolvedVariantStock(
                best.VariantSku,
                SafeStock(best.AvailableStock),
                StockSource.AutoBestVariant);
        }

        return new ResolvedVariantStock(null, 0, StockSource.FallbackZero);
    }

    private static int SafeStock(int value)
    {
        return Math.Max(0, value);
    }
}
