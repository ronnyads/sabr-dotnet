using Phub.Application.Stock;
using Phub.Domain.Entities;

namespace Phub.Api.Tests;

public sealed class VariantStockResolverTests
{
    [Fact]
    public void Resolve_ReturnsExactVariant_WhenRequestedVariantIsActive()
    {
        var variants = new List<ProductVariant>
        {
            BuildVariant("SKU-BASE-01", "SKU-BASE-01-A", 12, 20, isActive: true),
            BuildVariant("SKU-BASE-01", "SKU-BASE-01-B", 30, 40, isActive: true)
        };

        var resolved = VariantStockResolver.Resolve("SKU-BASE-01-A", variants);

        Assert.Equal("SKU-BASE-01-A", resolved.ResolvedVariantSku);
        Assert.Equal(12, resolved.AvailableStock);
        Assert.Equal(StockSource.ExactVariant, resolved.StockSource);
    }

    [Fact]
    public void Resolve_ReturnsAutoBestVariant_WhenRequestedDoesNotExist()
    {
        var variants = new List<ProductVariant>
        {
            BuildVariant("SKU-BASE-02", "SKU-BASE-02-A", 5, 10, isActive: true),
            BuildVariant("SKU-BASE-02", "SKU-BASE-02-B", 30, 35, isActive: true),
            BuildVariant("SKU-BASE-02", "SKU-BASE-02-C", 10, 10, isActive: true)
        };

        var resolved = VariantStockResolver.Resolve("SKU-BASE-02", variants);

        Assert.Equal("SKU-BASE-02-B", resolved.ResolvedVariantSku);
        Assert.Equal(30, resolved.AvailableStock);
        Assert.Equal(StockSource.AutoBestVariant, resolved.StockSource);
    }

    [Fact]
    public void Resolve_BreaksTieByPhysicalStock()
    {
        var variants = new List<ProductVariant>
        {
            BuildVariant("SKU-BASE-03", "SKU-BASE-03-A", 8, 9, isActive: true),
            BuildVariant("SKU-BASE-03", "SKU-BASE-03-B", 8, 22, isActive: true)
        };

        var resolved = VariantStockResolver.Resolve("SKU-BASE-03", variants);

        Assert.Equal("SKU-BASE-03-B", resolved.ResolvedVariantSku);
        Assert.Equal(8, resolved.AvailableStock);
        Assert.Equal(StockSource.AutoBestVariant, resolved.StockSource);
    }

    [Fact]
    public void Resolve_BreaksTieBySkuAscending()
    {
        var variants = new List<ProductVariant>
        {
            BuildVariant("SKU-BASE-04", "SKU-BASE-04-B", 9, 10, isActive: true),
            BuildVariant("SKU-BASE-04", "SKU-BASE-04-A", 9, 10, isActive: true)
        };

        var resolved = VariantStockResolver.Resolve("SKU-BASE-04", variants);

        Assert.Equal("SKU-BASE-04-A", resolved.ResolvedVariantSku);
        Assert.Equal(9, resolved.AvailableStock);
        Assert.Equal(StockSource.AutoBestVariant, resolved.StockSource);
    }

    [Fact]
    public void Resolve_ReturnsFallbackZero_WhenNoActiveVariantExists()
    {
        var variants = new List<ProductVariant>
        {
            BuildVariant("SKU-BASE-05", "SKU-BASE-05-A", 10, 20, isActive: false),
            BuildVariant("SKU-BASE-05", "SKU-BASE-05-B", 20, 30, isActive: false)
        };

        var resolved = VariantStockResolver.Resolve("SKU-BASE-05", variants);

        Assert.Null(resolved.ResolvedVariantSku);
        Assert.Equal(0, resolved.AvailableStock);
        Assert.Equal(StockSource.FallbackZero, resolved.StockSource);
    }

    private static ProductVariant BuildVariant(
        string baseSku,
        string variantSku,
        int availableStock,
        int physicalStock,
        bool isActive)
    {
        return new ProductVariant
        {
            BaseSku = baseSku,
            VariantSku = variantSku,
            Name = variantSku,
            CostPriceCents = 1000,
            CatalogPriceCents = 1500,
            AvailableStock = availableStock,
            PhysicalStock = physicalStock,
            ReservedStock = Math.Max(0, physicalStock - availableStock),
            IsActive = isActive
        };
    }
}
