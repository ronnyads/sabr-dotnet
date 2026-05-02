using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sabr.Application.Abstractions;
using Sabr.Application.Options;
using Microsoft.Extensions.Options;

namespace Sabr.Infrastructure.Integrations.Shopify;

public sealed class ShopifyApiClient : IShopifyApiClient
{
    private const string ApiVersion = "2024-01";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ShopifyOptions _options;
    private readonly ILogger<ShopifyApiClient> _logger;

    public ShopifyApiClient(
        HttpClient httpClient,
        IOptions<ShopifyOptions> options,
        ILogger<ShopifyApiClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    // ── OAuth ─────────────────────────────────────────────────────────────────

    public async Task<ShopifyTokenResponse> ExchangeCodeAsync(
        string shop,
        string code,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://{shop}/admin/oauth/access_token";
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["code"] = code
        });

        var response = await _httpClient.PostAsync(url, body, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), default, cancellationToken);
        var root = doc.RootElement;

        return new ShopifyTokenResponse
        {
            AccessToken = root.GetProperty("access_token").GetString() ?? string.Empty,
            Scope = root.TryGetProperty("scope", out var scopeProp)
                ? scopeProp.GetString() ?? string.Empty
                : string.Empty
        };
    }

    // ── Shop info ─────────────────────────────────────────────────────────────

    public async Task<ShopifyShopInfoResponse> GetShopInfoAsync(
        string shop,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://{shop}/admin/api/{ApiVersion}/shop.json";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Shopify-Access-Token", accessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), default, cancellationToken);
        var s = doc.RootElement.GetProperty("shop");

        return new ShopifyShopInfoResponse
        {
            ShopDomain = shop,
            Name = GetString(s, "name"),
            Email = GetString(s, "email"),
            Currency = GetString(s, "currency"),
            CountryCode = GetString(s, "country_code"),
            PlanName = GetString(s, "plan_name")
        };
    }

    // ── Orders ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ShopifyOrderDto>> GetOrdersAsync(
        string shop,
        string accessToken,
        DateTimeOffset? since = null,
        int limit = 250,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://{shop}/admin/api/{ApiVersion}/orders.json?limit={Math.Min(limit, 250)}&status=any";
        if (since.HasValue)
        {
            url += $"&updated_at_min={Uri.EscapeDataString(since.Value.ToString("O"))}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Shopify-Access-Token", accessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), default, cancellationToken);

        var orders = new List<ShopifyOrderDto>();
        if (!doc.RootElement.TryGetProperty("orders", out var ordersArray))
        {
            return orders;
        }

        foreach (var o in ordersArray.EnumerateArray())
        {
            var lineItems = new List<ShopifyLineItemDto>();
            if (o.TryGetProperty("line_items", out var lineItemsArray))
            {
                foreach (var li in lineItemsArray.EnumerateArray())
                {
                    lineItems.Add(new ShopifyLineItemDto
                    {
                        Id = GetLong(li, "id"),
                        Title = GetString(li, "title"),
                        Sku = GetString(li, "sku"),
                        Quantity = GetInt(li, "quantity"),
                        Price = GetDecimal(li, "price"),
                        VariantId = GetNullableLong(li, "variant_id"),
                        ProductId = GetNullableLong(li, "product_id")
                    });
                }
            }

            orders.Add(new ShopifyOrderDto
            {
                Id = GetLong(o, "id"),
                Name = GetString(o, "name"),
                Email = GetString(o, "email"),
                FinancialStatus = GetString(o, "financial_status"),
                FulfillmentStatus = GetString(o, "fulfillment_status"),
                TotalPrice = GetDecimal(o, "total_price"),
                Currency = GetString(o, "currency"),
                CreatedAt = GetDateTimeOffset(o, "created_at"),
                UpdatedAt = GetDateTimeOffset(o, "updated_at"),
                LineItems = lineItems
            });
        }

        return orders;
    }

    // ── Health ────────────────────────────────────────────────────────────────

    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        // Shopify has no unauthenticated ping — just verify the HTTP client is reachable
        await Task.CompletedTask;
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────

    private static string GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) ? p.GetString() ?? string.Empty : string.Empty;

    private static long GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.TryGetInt64(out var v) ? v : 0L;

    private static long? GetNullableLong(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Null) return null;
        return p.TryGetInt64(out var v) ? v : null;
    }

    private static int GetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : 0;

    private static decimal GetDecimal(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.TryGetDecimal(out var v) ? v : 0m;

    private static DateTimeOffset GetDateTimeOffset(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return DateTimeOffset.MinValue;
        var s = p.GetString();
        return DateTimeOffset.TryParse(s, out var dt) ? dt : DateTimeOffset.MinValue;
    }
}
