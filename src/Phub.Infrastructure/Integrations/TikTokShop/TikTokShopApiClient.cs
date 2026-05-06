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
    private const string AuthorizedShopsLegacyPath = "/authorization/202309/shops";
    private const string AuthorizedShopsCurrentPath = "/shop/202309/authorized_shops";
    private const string SearchPackagesCurrentPath = "/fulfillment/202309/packages/search";
    private const string SearchPackagesLegacyPath = "/fulfillment/search";
    private const string GetPackageDetailCurrentPath = "/fulfillment/202309/packages";
    private const string GetPackageDetailLegacyPath = "/fulfillment/detail";
    private const string GetPackageShippingDocumentCurrentPath = "/fulfillment/202309/packages/shipping_documents";
    private const string GetPackageShippingDocumentLegacyPath = "/fulfillment/shipping_document";

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

        await EnsureSuccessOrThrowAsync(response, "exchange_code", cancellationToken);
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

        await EnsureSuccessOrThrowAsync(response, "refresh_token", cancellationToken);
        return await response.Content.ReadFromJsonAsync<TikTokShopTokenEnvelope>(cancellationToken: cancellationToken)
               ?? new TikTokShopTokenEnvelope { Message = "Empty TikTok Shop response." };
    }

    public async Task<TikTokShopApiResponse<TikTokShopOrderSearchData>> SearchOrdersAsync(
        string accessToken,
        string appKey,
        string appSecret,
        DateTimeOffset from,
        DateTimeOffset to,
        string? shopCipher = null,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        const string path = "/order/202309/orders/search";

        var body = new Dictionary<string, object>
        {
            ["update_time_ge"] = from.ToUnixTimeSeconds(),
            ["update_time_lt"] = to.ToUnixTimeSeconds()
        };
        var bodyJson = JsonSerializer.Serialize(body);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var queryParams = BuildSignedOpenApiQueryParams(
            appKey,
            accessToken,
            timestamp,
            appSecret,
            path,
            shopCipher,
            new Dictionary<string, string>
            {
                ["page_size"] = "50",
                ["sort_field"] = "create_time",
                ["sort_order"] = "ASC"
            },
            bodyJson);

        if (!string.IsNullOrWhiteSpace(pageToken))
        {
            queryParams["page_token"] = pageToken;
        }

        var unsignedQueryParams = RemoveUnsignedQueryParams(queryParams);
        queryParams["sign"] = ComputeOpenApiSign(appSecret, path, unsignedQueryParams, bodyJson);

        var url = BuildUrl(path, queryParams);
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-tts-access-token", accessToken);
        request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowAsync(response, "search_orders", cancellationToken);

        return await response.Content.ReadFromJsonAsync<TikTokShopApiResponse<TikTokShopOrderSearchData>>(
                   cancellationToken: cancellationToken)
               ?? new TikTokShopApiResponse<TikTokShopOrderSearchData> { Message = "Empty response." };
    }

    public async Task<TikTokShopApiResponse<TikTokShopOrderDetailData>> GetOrderDetailAsync(
        string accessToken,
        string appKey,
        string appSecret,
        string[] orderIds,
        string? shopCipher = null,
        string? shopId = null,
        CancellationToken cancellationToken = default)
    {
        if (orderIds.Length == 0)
        {
            return new TikTokShopApiResponse<TikTokShopOrderDetailData>
            {
                Message = "Order ids are required.",
                Data = new TikTokShopOrderDetailData()
            };
        }

        TikTokShopApiException? lastApiException = null;
        foreach (var attempt in CreateOrderDetailAttempts(
                     accessToken,
                     appKey,
                     appSecret,
                     orderIds,
                     shopCipher,
                     shopId))
        {
            try
            {
                var response = await SendSignedRequestAsync(
                    attempt.Path,
                    attempt.Method,
                    accessToken,
                    attempt.QueryParams,
                    "get_order_detail",
                    cancellationToken,
                    attempt.BodyJson);

                var payload = await response.Content.ReadFromJsonAsync<TikTokShopApiResponse<TikTokShopOrderDetailData>>(
                                  cancellationToken: cancellationToken)
                              ?? new TikTokShopApiResponse<TikTokShopOrderDetailData> { Message = "Empty response." };

                if (attempt.AllowFallbackOnEmptyItems && OrdersContainNoLineItems(payload))
                {
                    continue;
                }

                return payload;
            }
            catch (TikTokShopApiException ex) when (attempt.AllowFallbackOnFailure)
            {
                lastApiException = ex;
            }
        }

        if (lastApiException is not null)
        {
            throw lastApiException;
        }

        return new TikTokShopApiResponse<TikTokShopOrderDetailData>
        {
            Code = 0,
            Message = "No order detail payload with imported items was returned by TikTok Shop.",
            Data = new TikTokShopOrderDetailData()
        };
    }

    public async Task<TikTokShopApiResponse<TikTokShopPackageSearchData>> SearchPackagesAsync(
        string accessToken,
        string appKey,
        string appSecret,
        DateTimeOffset from,
        DateTimeOffset to,
        string? shopCipher = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object>
        {
            ["update_time_from"] = from.ToUnixTimeSeconds(),
            ["update_time_to"] = to.ToUnixTimeSeconds(),
            ["page_size"] = 50,
            ["sort_by"] = 3,
            ["sort_type"] = 1
        };

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            body["cursor"] = cursor;
        }

        var bodyJson = JsonSerializer.Serialize(body);
        TikTokShopApiException? lastApiException = null;

        foreach (var attempt in CreatePackagePostAttempts(
                     accessToken,
                     appKey,
                     appSecret,
                     shopCipher,
                     SearchPackagesCurrentPath,
                     SearchPackagesLegacyPath,
                     bodyJson))
        {
            try
            {
                var response = await SendSignedRequestAsync(
                    attempt.Path,
                    HttpMethod.Post,
                    accessToken,
                    attempt.QueryParams,
                    "search_packages",
                    cancellationToken,
                    bodyJson);

                return await response.Content.ReadFromJsonAsync<TikTokShopApiResponse<TikTokShopPackageSearchData>>(
                           cancellationToken: cancellationToken)
                       ?? new TikTokShopApiResponse<TikTokShopPackageSearchData> { Message = "Empty response." };
            }
            catch (TikTokShopApiException ex) when (attempt.AllowFallbackOnFailure)
            {
                lastApiException = ex;
            }
        }

        if (lastApiException != null)
        {
            throw lastApiException;
        }

        throw new InvalidOperationException("Unable to search TikTok Shop packages.");
    }

    public async Task<TikTokShopApiResponse<TikTokShopPackageDetail>> GetPackageDetailAsync(
        string accessToken,
        string appKey,
        string appSecret,
        string packageId,
        string? shopCipher = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return new TikTokShopApiResponse<TikTokShopPackageDetail> { Message = "Package id is required." };
        }

        TikTokShopApiException? lastApiException = null;
        foreach (var attempt in CreatePackageGetAttempts(
                     accessToken,
                     appKey,
                     appSecret,
                     shopCipher,
                     GetPackageDetailCurrentPath,
                     GetPackageDetailLegacyPath,
                     new Dictionary<string, string> { ["package_id"] = packageId }))
        {
            try
            {
                var response = await SendSignedRequestAsync(
                    attempt.Path,
                    HttpMethod.Get,
                    accessToken,
                    attempt.QueryParams,
                    "get_package_detail",
                    cancellationToken);

                return await response.Content.ReadFromJsonAsync<TikTokShopApiResponse<TikTokShopPackageDetail>>(
                           cancellationToken: cancellationToken)
                       ?? new TikTokShopApiResponse<TikTokShopPackageDetail> { Message = "Empty response." };
            }
            catch (TikTokShopApiException ex) when (attempt.AllowFallbackOnFailure)
            {
                lastApiException = ex;
            }
        }

        if (lastApiException != null)
        {
            throw lastApiException;
        }

        throw new InvalidOperationException("Unable to load TikTok Shop package detail.");
    }

    public async Task<TikTokShopApiResponse<TikTokShopShippingDocumentData>> GetPackageShippingDocumentAsync(
        string accessToken,
        string appKey,
        string appSecret,
        string packageId,
        string? shopCipher = null,
        int documentType = 1,
        int documentSize = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return new TikTokShopApiResponse<TikTokShopShippingDocumentData> { Message = "Package id is required." };
        }

        TikTokShopApiException? lastApiException = null;
        foreach (var attempt in CreatePackageGetAttempts(
                     accessToken,
                     appKey,
                     appSecret,
                     shopCipher,
                     GetPackageShippingDocumentCurrentPath,
                     GetPackageShippingDocumentLegacyPath,
                     new Dictionary<string, string>
                     {
                         ["package_id"] = packageId,
                         ["document_type"] = documentType.ToString(),
                         ["document_size"] = documentSize.ToString()
                     }))
        {
            try
            {
                var response = await SendSignedRequestAsync(
                    attempt.Path,
                    HttpMethod.Get,
                    accessToken,
                    attempt.QueryParams,
                    "get_package_shipping_document",
                    cancellationToken);

                return await response.Content.ReadFromJsonAsync<TikTokShopApiResponse<TikTokShopShippingDocumentData>>(
                           cancellationToken: cancellationToken)
                       ?? new TikTokShopApiResponse<TikTokShopShippingDocumentData> { Message = "Empty response." };
            }
            catch (TikTokShopApiException ex) when (attempt.AllowFallbackOnFailure)
            {
                lastApiException = ex;
            }
        }

        if (lastApiException != null)
        {
            throw lastApiException;
        }

        throw new InvalidOperationException("Unable to load TikTok Shop package shipping document.");
    }

    public async Task PingAsync(
        string accessToken,
        string appKey,
        string appSecret,
        CancellationToken cancellationToken = default)
    {
        await ExecuteAuthorizedShopsRequestAsync(
            accessToken,
            appKey,
            appSecret,
            cancellationToken,
            parseResponse: false);
    }

    public async Task<IReadOnlyList<TikTokShopAuthorizedShop>> GetAuthorizedShopsAsync(
        string accessToken,
        string appKey,
        string appSecret,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAuthorizedShopsRequestAsync(
            accessToken,
            appKey,
            appSecret,
            cancellationToken,
            parseResponse: true);
    }

    public async Task<TikTokShopApiResponse<TikTokShopImageUploadData>> UploadImageFromUrlAsync(
        string imageUrl,
        string accessToken,
        string appKey,
        string appSecret,
        string? shopCipher = null,
        CancellationToken cancellationToken = default)
    {
        const string path = "/product/202309/images/upload";

        var body = new Dictionary<string, object>
        {
            ["img_url"] = imageUrl
        };
        var bodyJson = JsonSerializer.Serialize(body);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var queryParams = BuildSignedOpenApiQueryParams(
            appKey,
            accessToken,
            timestamp,
            appSecret,
            path,
            shopCipher,
            bodyJson: bodyJson);

        var url = BuildUrl(path, queryParams);
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-tts-access-token", accessToken);
        request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

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
        string? shopCipher = null,
        CancellationToken cancellationToken = default)
    {
        const string path = "/product/202309/products";

        var bodyJson = JsonSerializer.Serialize(request);
        var bodyDict = JsonSerializer.Deserialize<Dictionary<string, object>>(bodyJson) ?? [];

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var queryParams = BuildSignedOpenApiQueryParams(
            appKey,
            accessToken,
            timestamp,
            appSecret,
            path,
            shopCipher,
            bodyJson: bodyJson);

        var url = BuildUrl(path, queryParams);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Add("x-tts-access-token", accessToken);
        httpRequest.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

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
        string? shopCipher = null,
        int pageSize = 20,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        const string path = "/product/202309/products/search";

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var queryParams = BuildSignedOpenApiQueryParams(
            appKey,
            accessToken,
            timestamp,
            appSecret,
            path,
            shopCipher,
            new Dictionary<string, string>
            {
                ["page_size"] = pageSize.ToString()
            });

        if (!string.IsNullOrWhiteSpace(pageToken))
        {
            queryParams["page_token"] = pageToken;
        }

        var unsignedQueryParams = RemoveUnsignedQueryParams(queryParams);
        queryParams["sign"] = ComputeOpenApiSign(appSecret, path, unsignedQueryParams);

        var url = BuildUrl(path, queryParams);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-tts-access-token", accessToken);

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
        string? shopCipher = null,
        CancellationToken cancellationToken = default)
    {
        const string path = "/product/202309/categories";

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var queryParams = BuildSignedOpenApiQueryParams(
            appKey,
            accessToken,
            timestamp,
            appSecret,
            path,
            shopCipher,
            new Dictionary<string, string>
            {
                ["locale"] = "pt-BR"
            });

        var url = BuildUrl(path, queryParams);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-tts-access-token", accessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowAsync(response, "get_categories", cancellationToken);

        return await response.Content.ReadFromJsonAsync<TikTokShopApiResponse<TikTokShopCategoryData>>(
                   cancellationToken: cancellationToken)
               ?? new TikTokShopApiResponse<TikTokShopCategoryData> { Message = "Empty response." };
    }

    private static string ComputeOpenApiSign(
        string appSecret,
        string path,
        Dictionary<string, string> queryParams,
        string? bodyJson = null)
    {
        var sortedParams = new SortedDictionary<string, string>(queryParams, StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.Append(appSecret);
        sb.Append(path);
        foreach (var kv in sortedParams)
        {
            sb.Append(kv.Key);
            sb.Append(kv.Value);
        }

        if (!string.IsNullOrWhiteSpace(bodyJson))
        {
            sb.Append(bodyJson);
        }

        sb.Append(appSecret);

        var keyBytes = Encoding.UTF8.GetBytes(appSecret);
        var messageBytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hashBytes = HMACSHA256.HashData(keyBytes, messageBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static Dictionary<string, string> BuildBaseQueryParams(string appKey, string timestamp, string? shopCipher = null)
    {
        var d = new Dictionary<string, string>
        {
            ["app_key"] = appKey,
            ["timestamp"] = timestamp
        };
        if (!string.IsNullOrWhiteSpace(shopCipher))
        {
            d["shop_cipher"] = shopCipher;
        }

        return d;
    }

    private string BuildUrl(string path, Dictionary<string, string> queryParams)
    {
        var baseUrl = _options.ApiBaseUrl.TrimEnd('/');
        var query = string.Join("&", queryParams.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{baseUrl}{path}?{query}";
    }

    private IEnumerable<FulfillmentAttempt> CreatePackagePostAttempts(
        string accessToken,
        string appKey,
        string appSecret,
        string? shopCipher,
        string currentPath,
        string legacyPath,
        string bodyJson)
    {
        yield return new FulfillmentAttempt(
            currentPath,
            BuildSignedOpenApiQueryParams(
                appKey,
                accessToken,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                appSecret,
                currentPath,
                shopCipher,
                bodyJson: bodyJson),
            AllowFallbackOnFailure: true);

        yield return new FulfillmentAttempt(
            legacyPath,
            BuildSignedLegacyQueryParams(
                appKey,
                accessToken,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                appSecret,
                legacyPath,
                shopCipher,
                bodyJson: bodyJson),
            AllowFallbackOnFailure: false);
    }

    private IEnumerable<FulfillmentAttempt> CreatePackageGetAttempts(
        string accessToken,
        string appKey,
        string appSecret,
        string? shopCipher,
        string currentPath,
        string legacyPath,
        Dictionary<string, string> extraParams)
    {
        yield return new FulfillmentAttempt(
            currentPath,
            BuildSignedOpenApiQueryParams(
                appKey,
                accessToken,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                appSecret,
                currentPath,
                shopCipher,
                extraParams),
            AllowFallbackOnFailure: true);

        yield return new FulfillmentAttempt(
            legacyPath,
            BuildSignedLegacyQueryParams(
                appKey,
                accessToken,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                appSecret,
                legacyPath,
                shopCipher,
                extraParams: extraParams),
            AllowFallbackOnFailure: false);
    }

    private IEnumerable<OrderDetailAttempt> CreateOrderDetailAttempts(
        string accessToken,
        string appKey,
        string appSecret,
        string[] orderIds,
        string? shopCipher,
        string? shopId)
    {
        const string currentPath = "/order/202309/orders";
        const string legacyPath = "/api/orders/detail/query";

        var currentIdsBodyJson = JsonSerializer.Serialize(new Dictionary<string, string[]>
        {
            ["ids"] = orderIds
        });
        yield return new OrderDetailAttempt(
            currentPath,
            HttpMethod.Post,
            BuildSignedOpenApiQueryParams(
                appKey,
                accessToken,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                appSecret,
                currentPath,
                shopCipher,
                bodyJson: currentIdsBodyJson),
            currentIdsBodyJson,
            AllowFallbackOnFailure: true,
            AllowFallbackOnEmptyItems: true);

        var currentOrderIdListBodyJson = JsonSerializer.Serialize(new Dictionary<string, string[]>
        {
            ["order_id_list"] = orderIds
        });
        yield return new OrderDetailAttempt(
            currentPath,
            HttpMethod.Post,
            BuildSignedOpenApiQueryParams(
                appKey,
                accessToken,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                appSecret,
                currentPath,
                shopCipher,
                bodyJson: currentOrderIdListBodyJson),
            currentOrderIdListBodyJson,
            AllowFallbackOnFailure: true,
            AllowFallbackOnEmptyItems: true);

        var legacyExtraParams = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(shopId))
        {
            legacyExtraParams["shop_id"] = shopId;
        }

        yield return new OrderDetailAttempt(
            legacyPath,
            HttpMethod.Post,
            BuildSignedLegacyQueryParams(
                appKey,
                accessToken,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                appSecret,
                legacyPath,
                shopCipher: null,
                extraParams: legacyExtraParams,
                bodyJson: currentOrderIdListBodyJson),
            currentOrderIdListBodyJson,
            AllowFallbackOnFailure: false,
            AllowFallbackOnEmptyItems: false);
    }

    private async Task<HttpResponseMessage> SendSignedRequestAsync(
        string path,
        HttpMethod method,
        string accessToken,
        Dictionary<string, string> queryParams,
        string operation,
        CancellationToken cancellationToken,
        string? bodyJson = null)
    {
        var url = BuildUrl(path, queryParams);
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("x-tts-access-token", accessToken);

        if (!string.IsNullOrWhiteSpace(bodyJson))
        {
            request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        }

        var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowAsync(response, operation, cancellationToken);
        return response;
    }

    private static bool OrdersContainNoLineItems(TikTokShopApiResponse<TikTokShopOrderDetailData>? payload)
    {
        var orders = payload?.Data?.Orders;
        return orders is { Count: > 0 } && orders.All(order => order.LineItems.Count == 0);
    }

    private async Task<IReadOnlyList<TikTokShopAuthorizedShop>> ExecuteAuthorizedShopsRequestAsync(
        string accessToken,
        string appKey,
        string appSecret,
        CancellationToken cancellationToken,
        bool parseResponse)
    {
        TikTokShopApiException? lastApiException = null;

        foreach (var attempt in CreateAuthorizedShopsAttempts(accessToken, appKey, appSecret))
        {
            try
            {
                var response = await SendAuthorizedShopsRequestAsync(attempt, accessToken, cancellationToken);
                if (!parseResponse)
                {
                    return [];
                }

                var shops = await ParseAuthorizedShopsResponseAsync(response, cancellationToken);
                if (shops.Count > 0 || !attempt.AllowFallbackOnEmpty)
                {
                    return shops;
                }
            }
            catch (TikTokShopApiException ex) when (attempt.AllowFallbackOnFailure)
            {
                lastApiException = ex;
            }
        }

        if (lastApiException is not null)
        {
            throw lastApiException;
        }

        return [];
    }

    private IEnumerable<AuthorizedShopsAttempt> CreateAuthorizedShopsAttempts(
        string accessToken,
        string appKey,
        string appSecret)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var legacyQueryParams = new Dictionary<string, string>
        {
            ["app_key"] = appKey,
            ["timestamp"] = timestamp,
            ["version"] = "202309"
        };
        var legacySign = ComputeOpenApiSign(appSecret, AuthorizedShopsLegacyPath, legacyQueryParams);
        legacyQueryParams["access_token"] = accessToken;
        legacyQueryParams["sign"] = legacySign;

        yield return new AuthorizedShopsAttempt(
            AuthorizedShopsLegacyPath,
            legacyQueryParams,
            AllowFallbackOnFailure: true,
            AllowFallbackOnEmpty: true);

        var currentQueryParams = BuildBaseQueryParams(appKey, timestamp);
        currentQueryParams["sign"] = ComputeOpenApiSign(appSecret, AuthorizedShopsCurrentPath, currentQueryParams);
        yield return new AuthorizedShopsAttempt(
            AuthorizedShopsCurrentPath,
            currentQueryParams,
            AllowFallbackOnFailure: false,
            AllowFallbackOnEmpty: false);
    }

    private async Task<HttpResponseMessage> SendAuthorizedShopsRequestAsync(
        AuthorizedShopsAttempt attempt,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl(attempt.Path, attempt.QueryParams);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-tts-access-token", accessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowAsync(response, "authorized_shops", cancellationToken);
        return response;
    }

    private static async Task<IReadOnlyList<TikTokShopAuthorizedShop>> ParseAuthorizedShopsResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(rawBody);
        var root = document.RootElement;
        var metadata = ReadEnvelopeMetadata(root);
        if (metadata.Code.HasValue && metadata.Code.Value != 0)
        {
            throw new TikTokShopApiException(
                response.StatusCode,
                metadata.Code.Value.ToString(),
                metadata.Message,
                metadata.RequestId,
                rawBody,
                "authorized_shops");
        }

        return ExtractAuthorizedShops(root);
    }

    private static async Task EnsureSuccessOrThrowAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
        string? apiCode = null;
        string? apiMessage = null;
        string? requestId = null;

        if (!string.IsNullOrWhiteSpace(rawBody))
        {
            try
            {
                using var document = JsonDocument.Parse(rawBody);
                var metadata = ReadEnvelopeMetadata(document.RootElement);
                apiCode = metadata.Code?.ToString();
                apiMessage = metadata.Message;
                requestId = metadata.RequestId;
            }
            catch (JsonException)
            {
                // Preserve raw body when TikTok returns non-JSON payloads.
            }
        }

        throw new TikTokShopApiException(
            response.StatusCode,
            apiCode,
            apiMessage ?? $"{operation} failed with {(int)response.StatusCode} {response.StatusCode}.",
            requestId,
            rawBody,
            operation);
    }

    private static IReadOnlyList<TikTokShopAuthorizedShop> ExtractAuthorizedShops(JsonElement root)
    {
        if (!TryGetProperty(root, "data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (!TryGetArrayProperty(dataElement, out var arrayElement, "shops", "shop_list", "authorized_shops"))
        {
            return [];
        }

        var shops = new List<TikTokShopAuthorizedShop>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var shopId = ReadString(item, "shop_id", "id");
            var shopCipher = ReadString(item, "shop_cipher", "cipher");
            if (string.IsNullOrWhiteSpace(shopId) && string.IsNullOrWhiteSpace(shopCipher))
            {
                continue;
            }

            shops.Add(new TikTokShopAuthorizedShop
            {
                ShopId = shopId ?? string.Empty,
                ShopCipher = shopCipher ?? string.Empty,
                ShopName = ReadString(item, "shop_name", "name"),
                SellerName = ReadString(item, "seller_name"),
                SellerBaseRegion = ReadString(item, "seller_base_region", "base_region", "region")
            });
        }

        return shops;
    }

    private static (int? Code, string? Message, string? RequestId) ReadEnvelopeMetadata(JsonElement root)
    {
        int? code = null;
        if (TryGetProperty(root, "code", out var codeElement))
        {
            if (codeElement.ValueKind == JsonValueKind.Number && codeElement.TryGetInt32(out var numericCode))
            {
                code = numericCode;
            }
            else if (codeElement.ValueKind == JsonValueKind.String &&
                     int.TryParse(codeElement.GetString(), out var parsedCode))
            {
                code = parsedCode;
            }
        }

        return (
            code,
            ReadString(root, "message"),
            ReadString(root, "request_id"));
    }

    private static bool TryGetArrayProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out value) && value.ValueKind == JsonValueKind.Array)
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            if (value.ValueKind == JsonValueKind.Number)
            {
                return value.ToString();
            }
        }

        return null;
    }

    private static Dictionary<string, string> BuildSignedOpenApiQueryParams(
        string appKey,
        string accessToken,
        string timestamp,
        string appSecret,
        string path,
        string? shopCipher = null,
        Dictionary<string, string>? extraParams = null,
        string? bodyJson = null)
    {
        var unsignedQueryParams = BuildBaseQueryParams(appKey, timestamp, shopCipher);
        unsignedQueryParams["version"] = "202309";

        if (extraParams is not null)
        {
            foreach (var kv in extraParams)
            {
                unsignedQueryParams[kv.Key] = kv.Value;
            }
        }

        var sign = ComputeOpenApiSign(appSecret, path, unsignedQueryParams, bodyJson);
        var queryParams = new Dictionary<string, string>(unsignedQueryParams, StringComparer.Ordinal)
        {
            ["access_token"] = accessToken,
            ["sign"] = sign
        };

        return queryParams;
    }

    private static Dictionary<string, string> BuildSignedLegacyQueryParams(
        string appKey,
        string accessToken,
        string timestamp,
        string appSecret,
        string path,
        string? shopCipher = null,
        Dictionary<string, string>? extraParams = null,
        string? bodyJson = null)
    {
        var unsignedQueryParams = BuildBaseQueryParams(appKey, timestamp, shopCipher);
        if (extraParams is not null)
        {
            foreach (var kv in extraParams)
            {
                unsignedQueryParams[kv.Key] = kv.Value;
            }
        }

        var sign = ComputeOpenApiSign(appSecret, path, unsignedQueryParams, bodyJson);
        var queryParams = new Dictionary<string, string>(unsignedQueryParams, StringComparer.Ordinal)
        {
            ["access_token"] = accessToken,
            ["sign"] = sign
        };

        return queryParams;
    }

    private static Dictionary<string, string> RemoveUnsignedQueryParams(Dictionary<string, string> queryParams)
    {
        var filtered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in queryParams)
        {
            if (string.Equals(kv.Key, "access_token", StringComparison.Ordinal) ||
                string.Equals(kv.Key, "sign", StringComparison.Ordinal))
            {
                continue;
            }

            filtered[kv.Key] = kv.Value;
        }

        return filtered;
    }

    private sealed record AuthorizedShopsAttempt(
        string Path,
        Dictionary<string, string> QueryParams,
        bool AllowFallbackOnFailure,
        bool AllowFallbackOnEmpty);

    private sealed record FulfillmentAttempt(
        string Path,
        Dictionary<string, string> QueryParams,
        bool AllowFallbackOnFailure);

    private sealed record OrderDetailAttempt(
        string Path,
        HttpMethod Method,
        Dictionary<string, string> QueryParams,
        string? BodyJson,
        bool AllowFallbackOnFailure,
        bool AllowFallbackOnEmptyItems);
}
