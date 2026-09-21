using Phub.Application.Services;

namespace Phub.Api.Tests;

public sealed class InternalCatalogSkuPolicyTests
{
    [Theory]
    [InlineData("MLB7595215952")]
    [InlineData("MLBU5072958857")]
    [InlineData("mlbu5072958857")]
    public void MercadoLivreIdentifiersAreNotInternalSkus(string sku)
    {
        Assert.True(InternalCatalogSkuPolicy.IsMarketplaceExternalIdentifier(sku));
    }

    [Theory]
    [InlineData("PH-RN03")]
    [InlineData("MLBAG-01")]
    [InlineData("MLBU-01")]
    public void OtherSkusAreNotClassifiedAsMarketplaceIdentifiers(string sku)
    {
        Assert.False(InternalCatalogSkuPolicy.IsMarketplaceExternalIdentifier(sku));
    }
}
