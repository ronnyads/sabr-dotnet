using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Phub.Application.Options;
using Phub.Infrastructure.Integrations.TikTokShop;

namespace Phub.Api.Tests;

public sealed class TikTokShopApiClientTests
{
    [Fact]
    public async Task GetAuthorizedShopsAsync_UsesLegacyAuthorizationEndpointFirst_AndParsesOfficialPayload()
    {
        var handler = new RecordingHttpMessageHandler(request =>
        {
            Assert.Equal("/authorization/202309/shops", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-access", ReadQueryParam(request.RequestUri, "access_token"));
            Assert.Equal("202309", ReadQueryParam(request.RequestUri, "version"));
            Assert.False(string.IsNullOrWhiteSpace(ReadQueryParam(request.RequestUri, "sign")));
            Assert.Equal("test-access", request.Headers.GetValues("x-tts-access-token").Single());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "code": 0,
                      "message": "Success",
                      "request_id": "req-legacy",
                      "data": {
                        "shops": [
                          {
                            "cipher": "cipher-legacy",
                            "id": "7000714532876273420",
                            "name": "Legacy Shop",
                            "region": "BR"
                          }
                        ]
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var client = CreateApiClient(handler);
        var shops = await client.GetAuthorizedShopsAsync("test-access", "app-key", "app-secret");

        var shop = Assert.Single(shops);
        Assert.Equal("7000714532876273420", shop.ShopId);
        Assert.Equal("cipher-legacy", shop.ShopCipher);
        Assert.Equal("Legacy Shop", shop.ShopName);
        Assert.Equal("BR", shop.SellerBaseRegion);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetAuthorizedShopsAsync_FallsBackToCurrentEndpoint_WhenLegacyEndpointFails()
    {
        var handler = new RecordingHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/authorization/202309/shops")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        """
                        {
                          "code": 404,
                          "message": "legacy endpoint not found",
                          "request_id": "req-legacy-404"
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            Assert.Equal("/shop/202309/authorized_shops", request.RequestUri.AbsolutePath);
            Assert.Null(ReadQueryParam(request.RequestUri, "version"));
            Assert.Null(ReadQueryParam(request.RequestUri, "access_token"));
            Assert.False(string.IsNullOrWhiteSpace(ReadQueryParam(request.RequestUri, "sign")));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "code": 0,
                      "message": "Success",
                      "request_id": "req-current",
                      "data": {
                        "authorized_shops": [
                          {
                            "shop_id": "9001",
                            "shop_cipher": "cipher-current",
                            "shop_name": "Current Shop",
                            "seller_base_region": "US"
                          }
                        ]
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var client = CreateApiClient(handler);
        var shops = await client.GetAuthorizedShopsAsync("test-access", "app-key", "app-secret");

        var shop = Assert.Single(shops);
        Assert.Equal("9001", shop.ShopId);
        Assert.Equal("cipher-current", shop.ShopCipher);
        Assert.Equal("Current Shop", shop.ShopName);
        Assert.Equal("US", shop.SellerBaseRegion);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal("/authorization/202309/shops", request.RequestUri!.AbsolutePath),
            request => Assert.Equal("/shop/202309/authorized_shops", request.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task SearchOrdersAsync_SignsRequestUsingQueryParamsAndExactJsonBody()
    {
        var handler = new RecordingHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/order/202309/orders/search", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-access", ReadQueryParam(request.RequestUri, "access_token"));
            Assert.Equal("202309", ReadQueryParam(request.RequestUri, "version"));
            Assert.Equal("cipher-123", ReadQueryParam(request.RequestUri, "shop_cipher"));
            Assert.Equal("cursor-1", ReadQueryParam(request.RequestUri, "page_token"));
            Assert.Equal("50", ReadQueryParam(request.RequestUri, "page_size"));
            Assert.Equal("create_time", ReadQueryParam(request.RequestUri, "sort_field"));
            Assert.Equal("ASC", ReadQueryParam(request.RequestUri, "sort_order"));

            var bodyJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Equal("{\"update_time_ge\":1714850000,\"update_time_lt\":1714853600}", bodyJson);

            var expectedSign = ComputeExpectedSign(
                "app-secret",
                request.RequestUri.AbsolutePath,
                request.RequestUri,
                bodyJson);

            Assert.Equal(expectedSign, ReadQueryParam(request.RequestUri, "sign"));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "code": 0,
                      "message": "Success",
                      "request_id": "req-order-search",
                      "data": {
                        "orders": [],
                        "next_page_token": ""
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var client = CreateApiClient(handler);
        await client.SearchOrdersAsync(
            "test-access",
            "app-key",
            "app-secret",
            DateTimeOffset.FromUnixTimeSeconds(1714850000),
            DateTimeOffset.FromUnixTimeSeconds(1714853600),
            "cipher-123",
            "cursor-1");
    }

    private static TikTokShopApiClient CreateApiClient(HttpMessageHandler handler)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new TikTokShopOptions
        {
            ApiBaseUrl = "https://open-api.tiktokglobalshop.com"
        });

        return new TikTokShopApiClient(new HttpClient(handler), options);
    }

    private static string? ReadQueryParam(Uri uri, string key)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var pairKey = Uri.UnescapeDataString(pair[..idx]);
            if (!string.Equals(pairKey, key, StringComparison.Ordinal))
            {
                continue;
            }

            return Uri.UnescapeDataString(pair[(idx + 1)..]);
        }

        return null;
    }

    private static string ComputeExpectedSign(string appSecret, string path, Uri uri, string? bodyJson = null)
    {
        var queryParams = uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var idx = part.IndexOf('=');
                return new KeyValuePair<string, string>(
                    Uri.UnescapeDataString(part[..idx]),
                    Uri.UnescapeDataString(part[(idx + 1)..]));
            })
            .Where(kv => !string.Equals(kv.Key, "access_token", StringComparison.Ordinal) &&
                         !string.Equals(kv.Key, "sign", StringComparison.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal);

        var sb = new StringBuilder();
        sb.Append(appSecret);
        sb.Append(path);
        foreach (var kv in queryParams)
        {
            sb.Append(kv.Key);
            sb.Append(kv.Value);
        }

        if (!string.IsNullOrWhiteSpace(bodyJson))
        {
            sb.Append(bodyJson);
        }

        sb.Append(appSecret);
        var hashBytes = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }
}
