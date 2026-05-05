using Phub.Domain.ValueObjects;

namespace Phub.Api.Tests;

public sealed class SkuTests
{
    [Theory]
    [InlineData("abc-01", "ABC-01")]
    [InlineData(" stg/sku_003 ", "STG/SKU_003")]
    [InlineData("A", "A")]
    public void Parse_NormalizesToUppercase(string input, string expected)
    {
        var sku = Sku.Parse(input);
        Assert.Equal(expected, sku.Value);
    }

    [Theory]
    [InlineData("ábc")]
    [InlineData("-ABC")]
    [InlineData("A B")]
    [InlineData("ABC$01")]
    public void TryParse_InvalidValues_ReturnsFalse(string input)
    {
        var result = Sku.TryParse(input, out _);
        Assert.False(result);
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var first = Sku.Normalize("abc-01");
        var second = Sku.Normalize(first);
        Assert.Equal(first, second);
    }
}
