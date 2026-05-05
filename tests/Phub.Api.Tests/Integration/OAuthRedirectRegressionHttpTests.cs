using System.Net;
using Phub.Api.Tests.TestHost;

namespace Phub.Api.Tests.Integration;

public sealed class OAuthRedirectRegressionHttpTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public OAuthRedirectRegressionHttpTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task MercadoLivreCallback_MissingParameters_DoesNotRedirectToFileScheme()
    {
        using var client = _factory.CreateClient(new()
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/api/v1/client/integrations/mercadolivre/callback");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal(Uri.UriSchemeHttp, response.Headers.Location!.Scheme);
        Assert.DoesNotContain("file://", response.Headers.Location.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/client/integrations/mercadolivre", response.Headers.Location.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TikTokCallback_MissingParameters_DoesNotEmitFileSchemeInTopLevelRedirect()
    {
        using var client = _factory.CreateClient(new()
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/api/v1/client/integrations/tiktokshop/callback");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("http://localhost:4200/client/integrations/tiktokshop?tiktok=missing_code_or_state", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file://", html, StringComparison.OrdinalIgnoreCase);
    }
}
