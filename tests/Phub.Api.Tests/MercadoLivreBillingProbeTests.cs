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

    [Fact]
    public async Task BillingOrderDetails_ParsesConfirmedAmountsAndChargeIdentity()
    {
        const string body = """
        {"results":[{"order_id":2000001,"currency_info":{"currency_id":"BRL"},
          "sales_info":[{"operation_id":9001,"transaction_amount":100.00}],
          "sale_fee":{"net":12.50},"details":[{"charge_info":{"detail_id":77,
          "detail_amount":8.25,"detail_type":"CHARGE","detail_sub_type":"CXD",
          "debited_from_operation":"YES","creation_date_time":"2026-09-20T10:00:00-03:00"},
          "shipping_info":{"shipping_id":555}}]}]}
        """;
        Uri? requestedUri = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            requestedUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        })) { BaseAddress = new Uri("https://api.mercadolibre.com") };
        var api = new MercadoLivreApiClient(client, Microsoft.Extensions.Options.Options.Create(new MercadoLivreOptions()));

        var result = await api.GetBillingOrderDetailsAsync(["2000001"], "token");

        var order = Assert.Single(result.Orders);
        Assert.Equal("2000001", order.OrderId);
        Assert.Equal(9001, order.PaymentId);
        Assert.Equal(10_000, order.GrossAmountCents);
        Assert.Equal(1_250, order.SaleFeeNetCents);
        var charge = Assert.Single(order.Charges);
        Assert.Equal("77", charge.DetailId);
        Assert.Equal(825, charge.AmountCents);
        Assert.Equal("555", charge.ShipmentId);
        Assert.Contains("order_ids=2000001", requestedUri?.Query);
    }

    [Fact]
    public async Task BillingOrderDetails_HonorsProviderRetryAfter()
    {
        using var client = new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(7));
            return response;
        })) { BaseAddress = new Uri("https://api.mercadolibre.com") };
        var api = new MercadoLivreApiClient(client, Microsoft.Extensions.Options.Options.Create(new MercadoLivreOptions()));

        var result = await api.GetBillingOrderDetailsAsync(["1"], "token");

        Assert.True(result.RateLimited);
        Assert.Equal(TimeSpan.FromMinutes(7), result.RetryAfter);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
