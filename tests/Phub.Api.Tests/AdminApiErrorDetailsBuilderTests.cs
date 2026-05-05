using Phub.Api.Models;
using Phub.Application.Validation;

namespace Phub.Api.Tests;

public sealed class AdminApiErrorDetailsBuilderTests
{
    [Fact]
    public void Build_ExtractsInvalidCollectionsAndPreservesDetails()
    {
        var errors = new List<ValidationError>
        {
            new("invalidSkus", "SKU-001"),
            new("invalidSkus", "SKU-001"),
            new("invalidSkus", "SKU-002"),
            new("invalidPlanIds", "11111111-1111-1111-1111-111111111111"),
            new("invalidCatalogIds", "22222222-2222-2222-2222-222222222222"),
            new("productSkus", "One or more product SKUs are invalid")
        };

        var payload = AdminApiErrorDetailsBuilder.Build(errors);

        Assert.NotNull(payload.InvalidSkus);
        Assert.Equal(2, payload.InvalidSkus!.Count);
        Assert.Contains("SKU-001", payload.InvalidSkus);
        Assert.Contains("SKU-002", payload.InvalidSkus);

        Assert.NotNull(payload.InvalidPlanIds);
        Assert.Single(payload.InvalidPlanIds!);

        Assert.NotNull(payload.InvalidCatalogIds);
        Assert.Single(payload.InvalidCatalogIds!);

        Assert.Equal(errors.Count, payload.Details.Count);
    }
}
