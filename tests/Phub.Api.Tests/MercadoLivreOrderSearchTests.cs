using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Phub.Application.Options;
using Phub.Infrastructure.Integrations.MercadoLivre;

namespace Phub.Api.Tests;

public sealed class MercadoLivreOrderSearchTests
{
    [Fact]
    public async Task Search_orders_has_no_internal_ten_thousand_result_cap()
    {
        const int total = 10_050;
        var handler = new CallbackHandler(request =>
        {
            var offset = int.Parse(GetQuery(request.RequestUri!, "offset"));
            var count = Math.Min(50, total - offset);
            var ids = Enumerable.Range(offset, Math.Max(0, count)).Select(x => $"{{\"id\":\"{x}\"}}");
            var json = $"{{\"paging\":{{\"total\":{total}}},\"results\":[{string.Join(',', ids)}]}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        var result = await CreateClient(handler).SearchOrdersAsync(
            "123", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, "token");

        Assert.Equal(total, result.Count);
        Assert.Equal("10049", result[^1]);
    }

    [Fact]
    public async Task Search_orders_stops_when_provider_repeats_a_full_page()
    {
        var calls = 0;
        var handler = new CallbackHandler(_ =>
        {
            calls++;
            var ids = Enumerable.Range(0, 50).Select(x => $"{{\"id\":\"{x}\"}}");
            var json = $"{{\"paging\":{{\"total\":999999}},\"results\":[{string.Join(',', ids)}]}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        var result = await CreateClient(handler).SearchOrdersAsync(
            "123", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, "token");

        Assert.Equal(50, result.Count);
        Assert.Equal(2, calls);
    }

    private static MercadoLivreApiClient CreateClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.mercadolibre.com") };
        return new MercadoLivreApiClient(http, Microsoft.Extensions.Options.Options.Create(new MercadoLivreOptions
        {
            ClientId = "client", ClientSecret = "secret", RedirectUri = "https://example.test/callback",
            Resilience = new MercadoLivreResilienceOptions { RetryMaxAttempts = 1 }
        }));
    }

    private static string GetQuery(Uri uri, string key)
        => uri.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2))
            .Single(x => Uri.UnescapeDataString(x[0]) == key)[1];

    private sealed class CallbackHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => callback(request);
    }
}
