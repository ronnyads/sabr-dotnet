using Phub.Infrastructure.Storage;

namespace Phub.Api.Tests;

public sealed class StorageOptionsTests
{
    [Fact]
    public void GetPublicUrl_WithoutPublicBaseUrl_NormalizesToAbsoluteAppPath()
    {
        var options = new StorageOptions
        {
            PublicBaseUrl = string.Empty
        };

        var result = options.GetPublicUrl(@"product-images\SKU-001\image.png");

        Assert.Equal("/product-images/SKU-001/image.png", result);
    }

    [Fact]
    public void GetPublicUrl_WithPublicBaseUrl_AvoidsDuplicatedSlashes()
    {
        var options = new StorageOptions
        {
            PublicBaseUrl = "https://cdn.example.com/"
        };

        var result = options.GetPublicUrl("/product-images/SKU-001/image.png");

        Assert.Equal("https://cdn.example.com/product-images/SKU-001/image.png", result);
    }
}
