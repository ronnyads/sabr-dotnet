using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Api.Tests;

public sealed class MarketplaceListingCapabilityTests
{
    private static TenantMarketplaceListingMap Mapping() => new()
    {
        Id = Guid.NewGuid(),
        TenantId = "tenant-test",
        ClientId = Guid.NewGuid(),
        Provider = MarketplaceProvider.MercadoLivre,
        IntegrationId = Guid.NewGuid(),
        SellerId = 123,
        MlItemId = "MLB123",
        SabrVariantSku = "SKU-001",
        MappingVersion = 4
    };

    [Fact]
    public void Legacy_without_sales_allows_title_but_never_sku_or_stock()
    {
        var source = new MercadoLivreSellerItemDetails
        {
            ItemId = "MLB123",
            Title = "Produto",
            Status = "active",
            Price = 99.90m,
            SoldQuantity = 0
        };
        var adapter = new MercadoLivreLegacyListingAdapter();
        var listing = adapter.Normalize(Mapping(), source);
        var result = adapter.Evaluate(listing, source, DateTimeOffset.UtcNow);

        Assert.True(result.Fields["title"].Editable);
        Assert.True(result.Fields["price"].Editable);
        Assert.False(result.Fields["masterSku"].Editable);
        Assert.Equal("MASTER_SKU_IMMUTABLE", result.Fields["masterSku"].ReasonCode);
        Assert.False(result.Fields["stock"].Editable);
        Assert.Equal("CENTRAL_INVENTORY_MANAGED", result.Fields["stock"].ReasonCode);
    }

    [Fact]
    public void Legacy_with_sales_blocks_title()
    {
        var source = new MercadoLivreSellerItemDetails
        {
            ItemId = "MLB123",
            Title = "Produto",
            Status = "active",
            SoldQuantity = 1
        };
        var adapter = new MercadoLivreLegacyListingAdapter();
        var listing = adapter.Normalize(Mapping(), source);
        var result = adapter.Evaluate(listing, source, DateTimeOffset.UtcNow);

        Assert.False(result.Fields["title"].Editable);
        Assert.Equal("LISTING_HAS_SALES", result.Fields["title"].ReasonCode);
    }

    [Fact]
    public void UserProduct_uses_normalized_contract_and_blocks_inherited_title()
    {
        var source = new MercadoLivreSellerItemDetails
        {
            ItemId = "MLB123",
            UserProductId = "UP-99",
            Title = "Produto",
            Status = "active"
        };
        var adapter = new MercadoLivreUserProductListingAdapter();
        var listing = adapter.Normalize(Mapping(), source);
        var result = adapter.Evaluate(listing, source, DateTimeOffset.UtcNow);

        Assert.Equal(MarketplaceListingModel.UserProduct, listing.Model);
        Assert.Equal("UP-99", listing.Identity.UserProductId);
        Assert.False(result.Fields["title"].Editable);
        Assert.Equal("USER_PRODUCT_INHERITED_FIELD", result.Fields["title"].ReasonCode);
    }

    [Fact]
    public void Price_automation_blocks_price_editing()
    {
        var source = new MercadoLivreSellerItemDetails
        {
            ItemId = "MLB123",
            Title = "Produto",
            Status = "active",
            HasPriceAutomation = true
        };
        var adapter = new MercadoLivreLegacyListingAdapter();
        var listing = adapter.Normalize(Mapping(), source);
        var result = adapter.Evaluate(listing, source, DateTimeOffset.UtcNow);

        Assert.False(result.Fields["price"].Editable);
        Assert.Equal("PRICE_AUTOMATION_ACTIVE", result.Fields["price"].ReasonCode);
    }

    [Fact]
    public void Capability_hash_is_stable_inside_window_and_rotates_after_expiry()
    {
        var source = new MercadoLivreSellerItemDetails { ItemId = "MLB123", Title = "Produto", Status = "active" };
        var adapter = new MercadoLivreLegacyListingAdapter();
        var listing = adapter.Normalize(Mapping(), source);
        var first = adapter.Evaluate(listing, source, DateTimeOffset.FromUnixTimeSeconds(600));
        var sameWindow = adapter.Evaluate(listing, source, DateTimeOffset.FromUnixTimeSeconds(899));
        var nextWindow = adapter.Evaluate(listing, source, DateTimeOffset.FromUnixTimeSeconds(900));

        Assert.Equal(first.EvaluationHash, sameWindow.EvaluationHash);
        Assert.NotEqual(first.EvaluationHash, nextWindow.EvaluationHash);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(900), first.ExpiresAt);
    }

    [Fact]
    public void Warehouse_allocation_preserves_total_and_existing_proportion()
    {
        var result = StockAvailabilityService.AllocateWarehouseStock(
            new[]
            {
                new MercadoLivreUserProductStockLocation { StoreId = "B", Quantity = 1 },
                new MercadoLivreUserProductStockLocation { StoreId = "A", Quantity = 3 }
            },
            10).OrderBy(item => item.StoreId).ToArray();

        Assert.Equal(10, result.Sum(item => item.Quantity));
        Assert.Equal(8, result[0].Quantity);
        Assert.Equal(2, result[1].Quantity);
    }
}
