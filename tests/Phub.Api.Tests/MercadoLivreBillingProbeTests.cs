using System.Net;
using Phub.Application.Options;
using Phub.Infrastructure.Integrations.MercadoLivre;

namespace Phub.Api.Tests;

public sealed class MercadoLivreBillingProbeTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"results\":[]}", true, false, null)]
    [InlineData(HttpStatusCode.PartialContent, "{\"results\":[]}", true, false, null)]
    [InlineData(HttpStatusCode.OK, "<html>not billing</html>", false, false, "ML_BILLING_HTTP_200")]
    [InlineData(HttpStatusCode.Forbidden, "{}", false, false, "ML_BILLING_HTTP_403")]
    [InlineData(HttpStatusCode.TooManyRequests, "{}", false, true, "ML_BILLING_RATE_LIMITED")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", false, true, "ML_BILLING_UNAVAILABLE")]
    public async Task ProbeBillingPeriods_RequiresRealResponseAndClassifiesTransientFailures(
        HttpStatusCode status, string body, bool verified, bool transient, string? error)
    {
        Uri? requestedUri = null;
        string? authorization = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            requestedUri = request.RequestUri;
            authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        })) { BaseAddress = new Uri("https://api.mercadolibre.com") };
        var api = new MercadoLivreApiClient(client, Microsoft.Extensions.Options.Options.Create(new MercadoLivreOptions()));

        var result = await api.ProbeBillingPeriodsAsync("test-token");

        Assert.Equal(verified, result.Verified);
        Assert.Equal(transient, result.TransientFailure);
        Assert.Equal(error, result.ErrorCode);
        Assert.Equal("/billing/integration/monthly/periods", requestedUri?.AbsolutePath);
        Assert.Contains("group=ML", requestedUri?.Query);
        Assert.Contains("document_type=BILL", requestedUri?.Query);
        Assert.Equal("Bearer test-token", authorization);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
