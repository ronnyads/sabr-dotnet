using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Phub.Application.Abstractions;
using Phub.Application.Options;

namespace Phub.Infrastructure.Integrations.TikTokShop;

public sealed class TikTokShopApiClient : ITikTokShopApiClient
{
    private readonly HttpClient _httpClient;
    private readonly TikTokShopOptions _options;

    public TikTokShopApiClient(HttpClient httpClient, IOptions<TikTokShopOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<TikTokShopTokenEnvelope> ExchangeCodeAsync(
        string appKey,
        string appSecret,
        string authCode,
        CancellationToken cancellationToken = default)
    {
        var tokenUrl = $"{_options.AuthBaseUrl.TrimEnd('/')}{_options.TokenPath}";
        var response = await _httpClient.PostAsJsonAsync(
            tokenUrl,
            new
            {
                app_key = appKey,
                app_secret = appSecret,
                grant_type = "authorized_code",
                auth_code = authCode
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"TikTok token exchange failed: {(int)response.StatusCode} {response.StatusCode} — URL={tokenUrl} — Body={body}");
        }
        return await response.Content.ReadFromJsonAsync<TikTokShopTokenEnvelope>(cancellationToken: cancellationToken)
               ?? new TikTokShopTokenEnvelope { Message = "Empty TikTok Shop response." };
    }

    public async Task<TikTokShopTokenEnvelope> RefreshTokenAsync(
        string appKey,
        string appSecret,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var refreshUrl = $"{_options.AuthBaseUrl.TrimEnd('/')}{_options.RefreshTokenPath}";
        var response = await _httpClient.PostAsJsonAsync(
            refreshUrl,
            new
            {
                app_key = appKey,
                app_secret = appSecret,
                grant_type = "refresh_token",
                refresh_token = refreshToken
            },
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TikTokShopTokenEnvelope>(cancellationToken: cancellationToken)
               ?? new TikTokShopTokenEnvelope { Message = "Empty TikTok Shop response." };
    }

    public async Task<TikTokShopApiResponse<TikTokShopOrderSearchData>> SearchOrdersAsync(
        string accessToken,
        string appKey,
        string appSecret,
        DateTimeOffset from,
        DateTimeOffset to,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        const string path = "/order/202309/orders/search";

        var body = new Dictionary<string, object>
        {
            ["create_time_ge"] = from.ToUnixTimeSeconds(),
            ["create_time_lt"] = to.ToUnixTimeSeconds(),
            ["page_size"] = 50
        };

        if (!string.IsNullOrWhiteSpace(pageToken))
        {
            body["page_token"] = pageToken;
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var queryParams = BuildBaseQueryParams(appKey, timestamp);
        var sign = ComputeSign(appSecret, path, queryParams, body);
        queryParams["sign"] = sign;

        var url = BuildUrl(path, queryParams);
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-tts-access-token", accessToken);
        request.Content = JsonContent.Create(body);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TikTokShopApiResponse<TikTokShopOrderSearchData>>(
                   cancellationToken: cancellationToken)
               ?? new TikTokShopApiResponse<TikTokShopOrderSearchData> { Message = "Empty response." };
    }

    public async Task<TikTokShopApiResponse<TikTokShopOrderDetailData>> GetOrderDetailAsync(
        string accessToken,
        string appKey,
        string appSecret,
        string[] orderIds,
        CancellationToken cancellationToken = default)
    {
        const string path = "/order/202309/orders";

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var queryParams = BuildBaseQueryParams(appKey, timestamp);
        queryParams["ids"] = string.Join(",", orderIds);
        var sign = ComputeSign(appSecret, path, queryParams, null);
        queryParams["sign"] = sign;

        var url = BuildUrl(path, queryParams);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-tts-access-token", accessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TikTokShopApiResponse<TikTokShopOrderDetailData>>(
                   cancellationToken: cancellationToken)
               ?? new TikTokShopApiResponse<TikTokShopOrderDetailData> { Message = "Empty response." };
    }

    public async Task PingAsync(
        string accessToken,
        string appKey,
        string appSecret,
        CancellationToken cancellationToken = default)
    {
        const string path = "/shop/202309/authorized_shops";

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var queryParams = BuildBaseQueryParams(appKey, timestamp);
        var sign = ComputeSign(appSecret, path, queryParams, null);
        queryParams["sign"] = sign;

        var url = BuildUrl(path, queryParams);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-tts-access-token", accessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<TikTokShopApiResponse<TikTokShopImageUploadData>> UploadImageFromUrlAsync(
        string imageUrl,
        string accessToken,
        string appKey,
        string appSecret,
        CancellationToken cancellationToken = default)
    {
        const string path = "/product/202309/images/upload";

        var body = new Dictionary<string, object>
        {
            ["img_url"] = imageUrl
        };

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var queryParams = BuildBaseQueryParams(appKey, timestamp);
        var sign = ComputeSign(appSecret, path, queryParams, body);
        queryParams["sign"] = sign;

        var url = BuildUrl(path, queryParams);
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-tts-access-token", accessToken);
        request.Content = JsonContent.Create(body);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TikTokShopApiResponse<TikTokShopImageUploadData>>(
                   cancellationToken: cancellationToken)
               ?? new TikTokShopApiResponse<TikTokShopImageUploadData> { Message = "Empty response." };
    }

    public async Task<TikTokShopApiResponse<TikTokShopCreateProductData>> CreateProductAsync(
        TikTokShopCreateProductRequest request,
        string accessToken,
        string appKey,
        string appSecret,
        CancellationToken cancellationToken = default)
    {
        const string path = "/product/202309/products";

        var bodyJson = JsonSerializer.Serialize(request);
        var bodyDict = JsonSerializer.Deserialize<Dictionary<string, object>>(bodyJson) ?? [];

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var queryParams = BuildBaseQueryParams(appKey, timestamp);
        var sign = ComputeSign(appSecret, path, queryParams, bodyDict);
        queryParams["sign"] = sign;

        var url = BuildUrl(path, queryParams);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Add("x-tts-access-token", accessToken);
        httpRequest.Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TikTokShopApiResponse<TikTokShopCreateProductData>>(
                   cancellationToken: cancellationToken)
               ?? new TikTokShopApiResponse<TikTokShopCreateProductData> { Message = "Empty response." };
    }

    public async Task<TikTokShopApiResponse<TikTokShopProductListData>> GetProductsAsync(
        string accessToken,
        string appKey,
        string appSecret,
        int pageSize = 20,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        const string path = "/product/202309/products/search";

        var body = new Dictionary<string, object>
        {
            ["page_size"] = pageSize
        };

        if (!string.IsNullOrWhiteSpace(pageToken))
        {
            body["page_token"] = pageToken;
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var queryParams = BuildBaseQueryParams(appKey, timestamp);
        var sign = ComputeSign(appSecret, path, queryParams, body);
        queryParams["sign"] = sign;

        var url = BuildUrl(path, queryParams);
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-tts-access-token", accessToken);
        request.Content = JsonContent.Create(body);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TikTokShopApiResponse<TikTokShopProductListData>>(
                   cancellationToken: cancellationToken)
               ?? new TikTokShopApiResponse<TikTokShopProductListData> { Message = "Empty response." };
    }

    public async Task<TikTokShopApiResponse<TikTokShopCategoryData>> GetCategoriesAsync(
        string accessToken,
        string appKey,
        string appSecret,
        CancellationToken cancellationToken = default)
    {
        const string path = "/product/202309/categories";

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var queryParams = BuildBaseQueryParams(appKey, timestamp);
        queryParams["locale"] = "pt-BR";
        var sign = ComputeSign(appSecret, path, queryParams, null);
        queryParams["sign"] = sign;

        var url = BuildUrl(path, queryParams);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-tts-access-token", accessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TikTokShopApiResponse<TikTokShopCategoryData>>(
                   cancellationToken: cancellationToken)
               ?? new TikTokShopApiResponse<TikTokShopCategoryData> { Message = "Empty response." };
    }

    // TikTok Shop HMAC-SHA256 signing:
    // sign = HMAC-SHA256( secret, "{path}{sorted_param_key1}{value1}{sorted_param_key2}{value2}...{body_fields_sorted}" )
    private static string ComputeSign(
        string appSecret,
        string path,
        Dictionary<string, string> queryParams,
        Dictionary<string, object>? body)
    {
        var allParams = new SortedDictionary<string, string>(queryParams);

        if (body != null)
        {
            foreach (var kv in body)
            {
                allParams[kv.Key] = kv.Value?.ToString() ?? string.Empty;
            }
        }

        var sb = new StringBuilder();
        sb.Append(path);
        foreach (var kv in allParams)
        {
            sb.Append(kv.Key);
            sb.Append(kv.Value);
        }

        var keyBytes = Encoding.UTF8.GetBytes(appSecret);
        var messageBytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hashBytes = HMACSHA256.HashData(keyBytes, messageBytes);
        return Convert.ToHexString(hashBytes).ToUpperInvariant();
    }

    private static Dictionary<string, string> BuildBaseQueryParams(string appKey, string timestamp)
    {
        return new Dictionary<string, string>
        {
            ["app_key"] = appKey,
            ["timestamp"] = timestamp
        };
    }

    private string BuildUrl(string path, Dictionary<string, string> queryParams)
    {
        var baseUrl = _options.ApiBaseUrl.TrimEnd('/');
        var query = string.Join("&", queryParams.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{baseUrl}{path}?{query}";
    }
}
