using Phub.Application.Validation;

namespace Phub.Api.Tests;

public sealed class BrazilValidatorsTests
{
    [Fact]
    public void IsValidInscricaoEstadual_AcceptsPlausibleIssuedNumberWhenChecksumTableRejectsIt()
    {
        var result = BrazilValidators.IsValidInscricaoEstadual("155.203.127.118", "SP", false);

        Assert.True(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("111111111111")]
    public void IsValidInscricaoEstadual_RejectsMissingOrImplausibleNumbers(string value)
    {
        var result = BrazilValidators.IsValidInscricaoEstadual(value, "SP", false);

        Assert.False(result);
    }
}
