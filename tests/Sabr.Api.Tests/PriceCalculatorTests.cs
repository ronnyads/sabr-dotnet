using Sabr.Application.Services;
using Sabr.Domain.Enums;

namespace Sabr.Api.Tests;

public sealed class PriceCalculatorTests
{
    private readonly PriceCalculator _calculator = new();

    [Fact]
    public void ComputeFinalPrice_CatalogPrice_UsesBaseCatalogPrice()
    {
        var result = _calculator.ComputeFinalPrice(
            baseCatalogCents: 12990,
            pricingMode: PricingMode.CatalogPrice,
            markupPercent: null,
            fixedPriceCents: null);

        Assert.True(result.Succeeded);
        Assert.Equal(12990, result.Data);
    }

    [Fact]
    public void ComputeFinalPrice_MarkupPercent_RoundsAwayFromZero()
    {
        var result = _calculator.ComputeFinalPrice(
            baseCatalogCents: 101,
            pricingMode: PricingMode.MarkupPercent,
            markupPercent: 10.50m,
            fixedPriceCents: null);

        Assert.True(result.Succeeded);
        Assert.Equal(112, result.Data);
    }

    [Fact]
    public void ComputeFinalPrice_FixedPrice_UsesFixedPrice()
    {
        var result = _calculator.ComputeFinalPrice(
            baseCatalogCents: 8500,
            pricingMode: PricingMode.FixedPrice,
            markupPercent: null,
            fixedPriceCents: 9999);

        Assert.True(result.Succeeded);
        Assert.Equal(9999, result.Data);
    }

    [Fact]
    public void ComputeFinalPrice_InvalidMarkup_ReturnsValidationError()
    {
        var result = _calculator.ComputeFinalPrice(
            baseCatalogCents: 8500,
            pricingMode: PricingMode.MarkupPercent,
            markupPercent: 700,
            fixedPriceCents: null);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Field == "markupPercent");
    }

    [Fact]
    public void ComputeFinalPrice_InvalidFixedPrice_ReturnsValidationError()
    {
        var result = _calculator.ComputeFinalPrice(
            baseCatalogCents: 8500,
            pricingMode: PricingMode.FixedPrice,
            markupPercent: null,
            fixedPriceCents: -1);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Field == "fixedPriceCents");
    }
}
