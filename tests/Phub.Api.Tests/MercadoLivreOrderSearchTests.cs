using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Phub.Application.Abstractions;
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
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Search_order_page_clamps_to_production_maximum_limit()
    {
        var handler = new CallbackHandler(request =>
        {
            Assert.Equal("51", GetQuery(request.RequestUri!, "limit"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"paging\":{\"total\":0},\"results\":[]}", Encoding.UTF8, "application/json")
            });
        });

        var page = await CreateClient(handler).SearchOrdersPageAsync(
            "123", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow,
            offset: 0, limit: 1000, accessToken: "token");

        Assert.Equal(51, page.Limit);
    }

    [Fact]
    public async Task Search_order_page_preserves_provider_retry_after()
    {
        var handler = new CallbackHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":\"too_many_requests\"}", Encoding.UTF8, "application/json")
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
            return Task.FromResult(response);
        });

        var exception = await Assert.ThrowsAsync<MercadoLivreApiException>(() =>
            CreateClient(handler).SearchOrdersPageAsync(
                "123", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow,
                offset: 0, limit: 50, accessToken: "token"));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(17), exception.RetryAfter);
    }

    [Fact]
    public async Task Get_order_keeps_payment_and_provider_update_timestamps_distinct()
    {
        const string json = """
        {
          "id": "ORDER-1",
          "seller": { "id": 123 },
          "status": "cancelled",
          "date_created": "2026-09-10T10:00:00-03:00",
          "date_closed": null,
          "date_last_updated": "2026-10-02T11:30:00-03:00",
          "order_items": []
        }
        """;
        var handler = new CallbackHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));

        var result = await CreateClient(handler).GetOrderAsync("ORDER-1", "token");

        Assert.NotNull(result);
        Assert.Null(result!.PaidAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 14, 30, 0, TimeSpan.Zero),
            result.ProviderUpdatedAt?.ToUniversalTime());
    }

    [Fact]
    public async Task Search_order_page_also_queries_cancelled_stream_and_deduplicates_ids()
    {
        var handler = new CallbackHandler(request =>
        {
            var cancelled = request.RequestUri!.Query.Contains("order.status=cancelled", StringComparison.Ordinal);
            var json = cancelled
                ? "{\"paging\":{\"total\":2},\"results\":[{\"id\":\"2\"},{\"id\":\"3\"}]}"
                : "{\"paging\":{\"total\":2},\"results\":[{\"id\":\"1\"},{\"id\":\"2\"}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        var page = await CreateClient(handler).SearchOrdersPageAsync(
            "123", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow,
            offset: 0, limit: 50, accessToken: "token");

        Assert.Equal(new[] { "1", "2", "3" }, page.OrderIds);
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
