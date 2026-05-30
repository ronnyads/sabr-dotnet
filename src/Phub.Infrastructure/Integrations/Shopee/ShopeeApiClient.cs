using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Phub.Application.Abstractions;
using Phub.Application.Options;

namespace Phub.Infrastructure.Integrations.Shopee;

public sealed class ShopeeApiClient : IShopeeApiClient
{
    private const string ExchangeCodePath = "/api/v2/auth/token/get";
    private const string RefreshTokenPath = "/api/v2/auth/access_token/get";
    private const string ShopInfoPath = "/api/v2/shop/get_shop_info";
    private const string OrderListPath = "/api/v2/order/get_order_list";
    private const string OrderDetailPath = "/api/v2/order/get_order_detail";

    private readonly HttpClient _httpClient;
    private readonly ShopeeOptions _options;

    public ShopeeApiClient(HttpClient httpClient, IOptions<ShopeeOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<ShopeeTokenResponse> ExchangeCodeAsync(
        string code,
        long? shopId = null,
        long? mainAccountId = null,
        CancellationToken cancellationToken = default)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var query = BuildPublicQuery(ExchangeCodePath, timestamp);
        var url = BuildUrl(ExchangeCodePath, query);

        var body = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["partner_id"] = ParsePartnerId()
        };

        if (shopId.HasValue && shopId.Value > 0)
        {
            body["shop_id"] = shopId.Value;
        }

        if (mainAccountId.HasValue && mainAccountId.Value > 0)
        {
            body["main_account_id"] = mainAccountId.Value;
        }

        var response = await _httpClient.PostAsJsonAsync(url, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ShopeeTokenResponse>(cancellationToken: cancellationToken)
               ?? new ShopeeTokenResponse { Message = "Empty Shopee response." };
    }

    public async Task<ShopeeRefreshTokenResponse> RefreshAccessTokenAsync(
        string refreshToken,
        long shopId,
        CancellationToken cancellationToken = default)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var query = BuildPublicQuery(RefreshTokenPath, timestamp);
        var url = BuildUrl(RefreshTokenPath, query);

        var body = new Dictionary<string, object?>
        {
            ["refresh_token"] = refreshToken,
            ["partner_id"] = ParsePartnerId(),
            ["shop_id"] = shopId
        };

        var response = await _httpClient.PostAsJsonAsync(url, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ShopeeRefreshTokenResponse>(cancellationToken: cancellationToken)
               ?? new ShopeeRefreshTokenResponse { Message = "Empty Shopee response." };
    }

    public async Task<ShopeeApiEnvelope<ShopeeShopInfoResponse>> GetShopInfoAsync(
        string accessToken,
        long shopId,
        CancellationToken cancellationToken = default)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var query = BuildShopQuery(ShopInfoPath, timestamp, accessToken, shopId, null);
        var url = BuildUrl(ShopInfoPath, query);

        return await _httpClient.GetFromJsonAsync<ShopeeApiEnvelope<ShopeeShopInfoResponse>>(url, cancellationToken)
               ?? new ShopeeApiEnvelope<ShopeeShopInfoResponse> { Message = "Empty Shopee response." };
    }

    public async Task<ShopeeApiEnvelope<ShopeeOrderListResponse>> GetOrderListAsync(
        string accessToken,
        long shopId,
        long timeFrom,
        long timeTo,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var query = BuildShopQuery(
            OrderListPath,
            timestamp,
            accessToken,
            shopId,
            new Dictionary<string, string?>
            {
                ["time_range_field"] = "update_time",
                ["time_from"] = timeFrom.ToString(CultureInfo.InvariantCulture),
                ["time_to"] = timeTo.ToString(CultureInfo.InvariantCulture),
                ["page_size"] = "100",
                ["response_optional_fields"] = "order_status,booking_sn"
            });

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query["cursor"] = cursor;
        }

        var url = BuildUrl(OrderListPath, query);
        return await _httpClient.GetFromJsonAsync<ShopeeApiEnvelope<ShopeeOrderListResponse>>(url, cancellationToken)
               ?? new ShopeeApiEnvelope<ShopeeOrderListResponse> { Message = "Empty Shopee response." };
    }

    public async Task<ShopeeApiEnvelope<ShopeeOrderDetailResponse>> GetOrderDetailAsync(
        string accessToken,
        long shopId,
        IReadOnlyCollection<string> orderSnList,
        CancellationToken cancellationToken = default)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var query = BuildShopQuery(
            OrderDetailPath,
            timestamp,
            accessToken,
            shopId,
            new Dictionary<string, string?>
            {
                ["order_sn_list"] = string.Join(",", orderSnList),
                ["response_optional_fields"] = "item_list,pay_time,shipping_carrier,package_list,total_amount,payment_method,recipient_address"
            });

        var url = BuildUrl(OrderDetailPath, query);
        return await _httpClient.GetFromJsonAsync<ShopeeApiEnvelope<ShopeeOrderDetailResponse>>(url, cancellationToken)
               ?? new ShopeeApiEnvelope<ShopeeOrderDetailResponse> { Message = "Empty Shopee response." };
    }

    public Task PingAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    private Dictionary<string, string> BuildPublicQuery(string path, long timestamp)
    {
        var partnerId = _options.PartnerId.Trim();
        return new Dictionary<string, string>
        {
            ["partner_id"] = partnerId,
            ["timestamp"] = timestamp.ToString(CultureInfo.InvariantCulture),
            ["sign"] = ComputeHmac(partnerId + path + timestamp.ToString(CultureInfo.InvariantCulture))
        };
    }

    private Dictionary<string, string> BuildShopQuery(
        string path,
        long timestamp,
        string accessToken,
        long shopId,
        Dictionary<string, string?>? additionalQuery)
    {
        var partnerId = _options.PartnerId.Trim();
        var timestampString = timestamp.ToString(CultureInfo.InvariantCulture);
        var query = new Dictionary<string, string>
        {
            ["partner_id"] = partnerId,
            ["timestamp"] = timestampString,
            ["access_token"] = accessToken,
            ["shop_id"] = shopId.ToString(CultureInfo.InvariantCulture)
        };

        if (additionalQuery != null)
        {
            foreach (var entry in additionalQuery)
            {
                if (!string.IsNullOrWhiteSpace(entry.Value))
                {
                    query[entry.Key] = entry.Value!;
                }
            }
        }

        query["sign"] = ComputeHmac(partnerId + path + timestampString + accessToken + shopId.ToString(CultureInfo.InvariantCulture));
        return query;
    }

    private string BuildUrl(string path, IReadOnlyDictionary<string, string> query)
    {
        var baseUri = new Uri(_options.ApiBaseUrl.TrimEnd('/') + "/");
        var endpoint = new Uri(baseUri, path.TrimStart('/'));
        var queryString = string.Join("&", query.Select(entry =>
            $"{Uri.EscapeDataString(entry.Key)}={Uri.EscapeDataString(entry.Value)}"));
        return $"{endpoint}?{queryString}";
    }

    private string ComputeHmac(string value)
    {
        var keyBytes = Encoding.UTF8.GetBytes(_options.PartnerKey.Trim());
        var payloadBytes = Encoding.UTF8.GetBytes(value);
        using var hmac = new HMACSHA256(keyBytes);
        var hashBytes = hmac.ComputeHash(payloadBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private long ParsePartnerId()
    {
        if (long.TryParse(_options.PartnerId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var partnerId))
        {
            return partnerId;
        }

        throw new InvalidOperationException("Shopee PartnerId must be a valid integer.");
    }
}
