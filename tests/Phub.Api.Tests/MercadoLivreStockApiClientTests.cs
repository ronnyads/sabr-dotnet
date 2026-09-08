using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Phub.Application.Models;
using Phub.Application.Options;
using Phub.Infrastructure.Integrations.MercadoLivre;

namespace Phub.Api.Tests;

public sealed class MercadoLivreStockApiClientTests
{
    [Fact]
    public async Task Variation_stock_uses_item_resource_with_variations_payload()
    {
        var handler = new CallbackHandler(async request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/items/MLB123", request.RequestUri!.AbsolutePath);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var variation = json.RootElement.GetProperty("variations")[0];
            Assert.Equal(456L, variation.GetProperty("id").GetInt64());
            Assert.Equal(9, variation.GetProperty("available_quantity").GetInt32());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        await CreateClient(handler).UpdateVariationStockAsync("MLB123", "456", 9, "token");
    }

    [Fact]
    public async Task User_product_stock_requires_version_and_preserves_warehouse_identity()
    {
        var calls = 0;
        var handler = new CallbackHandler(async request =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/user-products/MLBU1/stock", request.RequestUri!.AbsolutePath);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"locations\":[{\"type\":\"seller_warehouse\",\"store_id\":\"10\",\"network_node_id\":\"SP1\",\"quantity\":4}]}",
                        Encoding.UTF8,
                        "application/json")
                };
                response.Headers.TryAddWithoutValidation("x-version", "17");
                return response;
            }

            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/user-products/MLBU1/stock/type/seller_warehouse", request.RequestUri!.AbsolutePath);
            Assert.Equal("17", request.Headers.GetValues("x-version").Single());
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var location = json.RootElement.GetProperty("locations")[0];
            Assert.Equal("10", location.GetProperty("store_id").GetString());
            Assert.Equal("SP1", location.GetProperty("network_node_id").GetString());
            Assert.Equal(7, location.GetProperty("quantity").GetInt32());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        var client = CreateClient(handler);

        var stock = await client.GetUserProductStockAsync("MLBU1", "token");
        Assert.Equal(17, stock.Version);
        var source = Assert.Single(stock.Locations);
        source.Quantity = 7;
        await client.UpdateUserProductWarehouseStockAsync("MLBU1", stock.Version, new[] { source }, "token");
        Assert.Equal(2, calls);
    }

    private static MercadoLivreApiClient CreateClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.mercadolibre.com") };
        return new MercadoLivreApiClient(http, Microsoft.Extensions.Options.Options.Create(new MercadoLivreOptions
        {
            ClientId = "client",
            ClientSecret = "secret",
            RedirectUri = "https://example.test/callback",
            Resilience = new MercadoLivreResilienceOptions { RetryMaxAttempts = 1 }
        }));
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => callback(request);
    }
}
