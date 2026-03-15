using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Options;

namespace Sabr.Infrastructure.Integrations.MercadoLivre;

public sealed class MercadoLivreApiClient : IMercadoLivreApiClient
{
    private static readonly object CircuitLock = new();
    private static int _consecutiveTransientFailures;
    private static DateTimeOffset _circuitOpenUntil = DateTimeOffset.MinValue;

    private readonly HttpClient _httpClient;
    private readonly MercadoLivreOptions _options;

    public MercadoLivreApiClient(
        HttpClient httpClient,
        IOptions<MercadoLivreOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<MercadoLivreTokenResponse> ExchangeCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        return await ExecuteWithResilienceAsync(async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = _options.ClientId,
                    ["client_secret"] = _options.ClientSecret,
                    ["code"] = code,
                    ["redirect_uri"] = _options.RedirectUri
                })
            };

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return new MercadoLivreTokenResponse
            {
                AccessToken = GetRequiredString(doc.RootElement, "access_token"),
                RefreshToken = GetRequiredString(doc.RootElement, "refresh_token"),
                ExpiresInSeconds = GetRequiredInt(doc.RootElement, "expires_in")
            };
        }, cancellationToken);
    }

    public async Task<MercadoLivreTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        return await ExecuteWithResilienceAsync(async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = _options.ClientId,
                    ["client_secret"] = _options.ClientSecret,
                    ["refresh_token"] = refreshToken
                })
            };

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return new MercadoLivreTokenResponse
            {
                AccessToken = GetRequiredString(doc.RootElement, "access_token"),
                RefreshToken = GetRequiredString(doc.RootElement, "refresh_token"),
                ExpiresInSeconds = GetRequiredInt(doc.RootElement, "expires_in")
            };
        }, cancellationToken);
    }

    public async Task<MercadoLivreUserMeResponse> GetUserMeAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        return await ExecuteWithResilienceAsync(async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/users/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var id = GetOptionalString(doc.RootElement, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException("Mercado Livre /users/me did not return id.");
            }

            return new MercadoLivreUserMeResponse
            {
                SellerId = id,
                Nickname = GetOptionalString(doc.RootElement, "nickname")
            };
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> SearchOrdersAsync(
        string sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithResilienceAsync(async ct =>
        {
            var requestUri =
                $"/orders/search?seller={Uri.EscapeDataString(sellerId)}" +
                $"&order.date_created.from={Uri.EscapeDataString(from.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}" +
                $"&order.date_created.to={Uri.EscapeDataString(to.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}";

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var orderIds = new List<string>();
            if (doc.RootElement.TryGetProperty("results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in resultsElement.EnumerateArray())
                {
                    var id = GetOptionalString(entry, "id");
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        orderIds.Add(id);
                    }
                }
            }

            return (IReadOnlyList<string>)orderIds;
        }, cancellationToken);
    }

    public async Task<MercadoLivreOrderDetails?> GetOrderAsync(
        string orderId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithResilienceAsync(async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/orders/{Uri.EscapeDataString(orderId)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var shipmentId = root.TryGetProperty("shipping", out var shippingElement)
                ? GetOptionalString(shippingElement, "id")
                : null;

            var paidAt = TryParseDateTimeOffset(
                GetOptionalString(root, "date_closed") ??
                GetOptionalString(root, "date_last_updated"));

            var shippingMode = root.TryGetProperty("shipping", out var shippingRoot)
                ? GetOptionalString(shippingRoot, "mode")
                : null;
            var logisticType = root.TryGetProperty("shipping", out var shippingRoot2)
                ? GetOptionalString(shippingRoot2, "logistic_type")
                : null;
            var shipByDeadlineAt = root.TryGetProperty("shipping", out var shippingRoot3)
                ? TryParseDateTimeOffset(
                    GetOptionalString(shippingRoot3, "shipping_estimated_date") ??
                    GetOptionalString(shippingRoot3, "shipping_deadline") ??
                    GetOptionalString(shippingRoot3, "date_first_printed"))
                : null;

            var items = new List<MercadoLivreOrderItemDetails>();
            if (root.TryGetProperty("order_items", out var orderItemsElement) && orderItemsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var orderItem in orderItemsElement.EnumerateArray())
                {
                    var quantity = 0;
                    if (orderItem.TryGetProperty("quantity", out var quantityElement))
                    {
                        quantity = ParseInt(quantityElement);
                    }

                    if (!orderItem.TryGetProperty("item", out var itemElement))
                    {
                        continue;
                    }

                    var itemId = GetOptionalString(itemElement, "id");
                    if (string.IsNullOrWhiteSpace(itemId) || quantity <= 0)
                    {
                        continue;
                    }

                    items.Add(new MercadoLivreOrderItemDetails
                    {
                        MlItemId = itemId,
                        MlVariationId = GetOptionalString(itemElement, "variation_id"),
                        Quantity = quantity,
                        RawJson = orderItem.GetRawText()
                    });
                }
            }

            return new MercadoLivreOrderDetails
            {
                MlOrderId = GetOptionalString(root, "id") ?? orderId,
                SellerId = ResolveOrderSellerId(root),
                Status = GetOptionalString(root, "status") ?? string.Empty,
                PaidAt = paidAt,
                ShipmentId = shipmentId,
                ShippingMode = shippingMode,
                LogisticType = logisticType,
                ShipByDeadlineAt = shipByDeadlineAt,
                RawJson = json,
                Items = items
            };
        }, cancellationToken);
    }

    public async Task<MercadoLivreShipmentDetails?> GetShipmentAsync(
        string shipmentId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithResilienceAsync(async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/shipments/{Uri.EscapeDataString(shipmentId)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new MercadoLivreShipmentDetails
            {
                ShipmentId = GetOptionalString(root, "id") ?? shipmentId,
                SellerId = ResolveShipmentSellerId(root),
                Status = GetOptionalString(root, "status"),
                Substatus = GetOptionalString(root, "substatus"),
                ShippingMode = GetOptionalString(root, "mode"),
                LogisticType = GetOptionalString(root, "logistic_type"),
                TrackingNumber = root.TryGetProperty("tracking_number", out var trackingNumberElement)
                    ? GetOptionalString(root, "tracking_number")
                    : (root.TryGetProperty("tracking", out var trackingElement) ? GetOptionalString(trackingElement, "id") : null),
                TrackingMethod = root.TryGetProperty("tracking_method", out var trackingMethodElement)
                    ? GetOptionalString(root, "tracking_method")
                    : (root.TryGetProperty("tracking", out var trackingElement2) ? GetOptionalString(trackingElement2, "method") : null),
                TrackingUrl = root.TryGetProperty("tracking", out var trackingRoot)
                    ? GetOptionalString(trackingRoot, "url")
                    : null,
                ShippedAt = TryParseDateTimeOffset(
                    GetOptionalString(root, "date_shipped") ??
                    GetOptionalString(root, "date_handling")),
                ShipByDeadlineAt = TryParseDateTimeOffset(
                    GetOptionalString(root, "shipping_estimated_date") ??
                    GetOptionalString(root, "shipping_deadline") ??
                    GetOptionalString(root, "date_first_printed")),
                RawJson = json
            };
        }, cancellationToken);
    }

    public async Task<MercadoLivreShipmentLabelResult?> GetShipmentLabelAsync(
        string shipmentId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithResilienceAsync(async ct =>
        {
            // TODO: confirm best label endpoint/format for each ML shipping mode.
            var requestUri = $"/shipment_labels?shipment_ids={Uri.EscapeDataString(shipmentId)}&response_type=pdf";
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length == 0)
                {
                    return null;
                }

                return new MercadoLivreShipmentLabelResult
                {
                    ShipmentId = shipmentId,
                    SourceUrl = requestUri,
                    ContentType = string.IsNullOrWhiteSpace(mediaType) ? "application/pdf" : mediaType,
                    Content = bytes,
                    Sha256 = ComputeSha256(bytes)
                };
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var labelUrl = TryExtractLabelUrl(body);
            if (string.IsNullOrWhiteSpace(labelUrl))
            {
                return null;
            }

            using var labelRequest = new HttpRequestMessage(HttpMethod.Get, labelUrl);
            labelRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var labelResponse = await _httpClient.SendAsync(labelRequest, ct);
            labelResponse.EnsureSuccessStatusCode();

            var labelBytes = await labelResponse.Content.ReadAsByteArrayAsync(ct);
            if (labelBytes.Length == 0)
            {
                return null;
            }

            var labelMediaType = labelResponse.Content.Headers.ContentType?.MediaType;
            return new MercadoLivreShipmentLabelResult
            {
                ShipmentId = shipmentId,
                SourceUrl = labelUrl,
                ContentType = string.IsNullOrWhiteSpace(labelMediaType) ? "application/pdf" : labelMediaType,
                Content = labelBytes,
                Sha256 = ComputeSha256(labelBytes)
            };
        }, cancellationToken);
    }

    public async Task<MercadoLivreCreateItemResult> CreateItemAsync(
        MercadoLivreCreateItemRequest request,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithResilienceAsync(async ct =>
        {
            var payload = new Dictionary<string, object?>
            {
                ["title"] = request.Title,
                ["category_id"] = request.CategoryId,
                ["price"] = request.Price,
                ["currency_id"] = string.IsNullOrWhiteSpace(request.CurrencyId) ? "BRL" : request.CurrencyId,
                ["available_quantity"] = Math.Max(0, request.AvailableQuantity),
                ["buying_mode"] = string.IsNullOrWhiteSpace(request.BuyingMode) ? "buy_it_now" : request.BuyingMode,
                ["listing_type_id"] = string.IsNullOrWhiteSpace(request.ListingTypeId) ? "gold_special" : request.ListingTypeId,
                ["condition"] = string.IsNullOrWhiteSpace(request.Condition) ? "new" : request.Condition,
                ["seller_custom_field"] = request.SellerCustomField,
                ["attributes"] = BuildCreateItemAttributes(request),
                ["pictures"] = request.PictureUrls
                    .Where(url => !string.IsNullOrWhiteSpace(url))
                    .Select(url => new { source = url.Trim() })
                    .ToArray()
            };
            if (request.Variations.Count > 0)
            {
                payload["variations"] = request.Variations.Select(variation => new
                {
                    available_quantity = Math.Max(0, variation.AvailableQuantity),
                    attribute_combinations = variation.AttributeCombinations.Select(attr => new
                    {
                        id = attr.Id,
                        value_id = attr.ValueId,
                        value_name = attr.ValueName
                    }).ToArray(),
                    seller_custom_field = variation.SabrVariantSku,
                    picture_ids = variation.PictureUrls.Where(url => !string.IsNullOrWhiteSpace(url)).ToArray(),
                    price = variation.Price
                }).ToArray();
            }
            if (!string.IsNullOrWhiteSpace(request.Description))
            {
                payload["description"] = request.Description;
            }

            // TODO: confirm the exact required item creation payload for each ML category/listing type.
            var rawPayload = JsonSerializer.Serialize(payload);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/items")
            {
                Content = new StringContent(rawPayload, Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            await EnsureSuccessOrThrowApiExceptionAsync(response, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var itemId = GetRequiredString(doc.RootElement, "id");
            string? variationId = null;
            if (doc.RootElement.TryGetProperty("variations", out var variationsElement)
                && variationsElement.ValueKind == JsonValueKind.Array
                && variationsElement.GetArrayLength() > 0)
            {
                variationId = GetOptionalString(variationsElement[0], "id");
            }

            var variationResults = new List<MercadoLivreCreateItemVariationResult>();
            if (doc.RootElement.TryGetProperty("variations", out var responseVariations)
                && responseVariations.ValueKind == JsonValueKind.Array)
            {
                foreach (var variation in responseVariations.EnumerateArray())
                {
                    variationResults.Add(new MercadoLivreCreateItemVariationResult
                    {
                        VariationId = GetOptionalString(variation, "id"),
                        SabrVariantSku = GetOptionalString(variation, "seller_custom_field")
                                         ?? ExtractVariationSku(variation)
                    });
                }
            }

            return new MercadoLivreCreateItemResult
            {
                ItemId = itemId,
                VariationId = variationId,
                Permalink = GetOptionalString(doc.RootElement, "permalink"),
                ApiUrl = $"/items/{Uri.EscapeDataString(itemId)}",
                Variations = variationResults
            };
        }, cancellationToken);
    }

    private static object[] BuildCreateItemAttributes(MercadoLivreCreateItemRequest request)
    {
        var attributes = new Dictionary<string, (string? valueId, string? valueName)>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in request.Attributes)
        {
            var id = attribute.Id?.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var valueId = string.IsNullOrWhiteSpace(attribute.ValueId) ? null : attribute.ValueId.Trim();
            var valueName = string.IsNullOrWhiteSpace(attribute.ValueName) ? null : attribute.ValueName.Trim();
            if (string.IsNullOrWhiteSpace(valueId) && string.IsNullOrWhiteSpace(valueName))
            {
                continue;
            }

            attributes[id] = (valueId, valueName);
        }

        if (!attributes.ContainsKey("SELLER_SKU") && !string.IsNullOrWhiteSpace(request.SabrVariantSku))
        {
            attributes["SELLER_SKU"] = (null, request.SabrVariantSku.Trim());
        }

        return attributes
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => new
            {
                id = item.Key,
                value_id = item.Value.valueId,
                value_name = item.Value.valueName
            })
            .ToArray<object>();
    }

    public async Task<MercadoLivreFeeEstimateResponse> EstimateFeesAsync(
        MercadoLivreFeeEstimateRequest request,
        string? siteId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithResilienceAsync(async ct =>
        {
            var safeSiteId = string.IsNullOrWhiteSpace(siteId) ? "MLB" : siteId.Trim().ToUpperInvariant();
            var listingType = string.IsNullOrWhiteSpace(request.ListingTypeId) ? "gold_special" : request.ListingTypeId.Trim();
            var categoryId = request.CategoryId.Trim();
            var currencyId = string.IsNullOrWhiteSpace(request.CurrencyId) ? "BRL" : request.CurrencyId.Trim().ToUpperInvariant();
            var price = Math.Round(request.Price, 2, MidpointRounding.AwayFromZero);
            var requestUri =
                $"/sites/{Uri.EscapeDataString(safeSiteId)}/listing_prices?price={Uri.EscapeDataString(price.ToString("0.00", CultureInfo.InvariantCulture))}" +
                $"&listing_type_id={Uri.EscapeDataString(listingType)}" +
                $"&category_id={Uri.EscapeDataString(categoryId)}" +
                $"&currency_id={Uri.EscapeDataString(currencyId)}";

            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, requestUri);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(httpRequest, ct);
            await EnsureSuccessOrThrowApiExceptionAsync(response, ct);

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var node = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                ? root[0]
                : root;

            var saleFee = GetOptionalDecimal(node, "sale_fee_amount")
                          ?? GetOptionalDecimal(node, "sale_fee")
                          ?? 0m;
            var fixedFee = GetOptionalDecimal(node, "fixed_fee")
                           ?? GetOptionalDecimal(node, "fixed_fee_amount")
                           ?? 0m;
            var total = GetOptionalDecimal(node, "total_fee")
                        ?? GetOptionalDecimal(node, "total_fee_amount")
                        ?? saleFee + fixedFee;

            return new MercadoLivreFeeEstimateResponse
            {
                SaleFeeAmount = Math.Round(saleFee, 2, MidpointRounding.AwayFromZero),
                FixedFeeAmount = Math.Round(fixedFee, 2, MidpointRounding.AwayFromZero),
                TotalFeeAmount = Math.Round(total, 2, MidpointRounding.AwayFromZero),
                RawJson = json
            };
        }, cancellationToken);
    }

    public async Task<MercadoLivreCategoryCapabilityResponse> GetCategoryCapabilitiesAsync(
        string categoryId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithResilienceAsync(async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/categories/{Uri.EscapeDataString(categoryId)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request, ct);
            await EnsureSuccessOrThrowApiExceptionAsync(response, ct);

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement settings = default;
            if (root.TryGetProperty("settings", out var settingsNode))
            {
                settings = settingsNode;
            }

            var allowsVariations = false;
            var maxVariationsAllowed = 0;
            var maxVariationAttributes = 0;
            var allowedVariationAttributes = new List<string>();
            var categoryName = GetOptionalString(root, "name")?.Trim();
            var categoryPathFromRoot = ResolveCategoryPathFromRoot(root, categoryName);
            var isLeaf = true;
            if (root.TryGetProperty("children_categories", out var childrenCategoriesElement)
                && childrenCategoriesElement.ValueKind == JsonValueKind.Array)
            {
                isLeaf = childrenCategoriesElement.GetArrayLength() == 0;
            }

            if (settings.ValueKind == JsonValueKind.Object)
            {
                allowsVariations = GetOptionalBool(settings, "variations")
                                   ?? GetOptionalBool(settings, "allow_variations")
                                   ?? false;
                maxVariationsAllowed = GetOptionalInt(settings, "max_variations_allowed")
                                       ?? GetOptionalInt(settings, "max_variations")
                                       ?? 0;
                maxVariationAttributes = GetOptionalInt(settings, "max_variation_attributes")
                                         ?? 0;
                if (settings.TryGetProperty("variation_attributes", out var variationAttributesElement)
                    && variationAttributesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var attribute in variationAttributesElement.EnumerateArray())
                    {
                        var value = attribute.ValueKind == JsonValueKind.String
                            ? attribute.GetString()
                            : GetOptionalString(attribute, "id");
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            allowedVariationAttributes.Add(value.Trim());
                        }
                    }
                }
            }

            return new MercadoLivreCategoryCapabilityResponse
            {
                CategoryName = categoryName,
                CategoryPathFromRoot = categoryPathFromRoot,
                IsLeaf = isLeaf,
                AllowsVariations = allowsVariations,
                MaxVariationsAllowed = maxVariationsAllowed,
                MaxVariationAttributes = maxVariationAttributes,
                AllowedVariationAttributes = allowedVariationAttributes
            };
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<MercadoLivreCategoryAttributeResponse>> GetCategoryAttributesAsync(
        string categoryId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithResilienceAsync(async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/categories/{Uri.EscapeDataString(categoryId)}/attributes");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request, ct);
            await EnsureSuccessOrThrowApiExceptionAsync(response, ct);

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var attributes = new List<MercadoLivreCategoryAttributeResponse>();
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return (IReadOnlyList<MercadoLivreCategoryAttributeResponse>)attributes;
            }

            foreach (var node in doc.RootElement.EnumerateArray())
            {
                var id = GetOptionalString(node, "id");
                var name = GetOptionalString(node, "name");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var required = false;
                var conditional = false;
                var isVariation = false;
                var parsedTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (node.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                {
                    required = GetOptionalBool(tags, "required") == true;
                    conditional = GetOptionalBool(tags, "conditional_required") == true
                                  || GetOptionalBool(tags, "required_if") == true;
                    isVariation = GetOptionalBool(tags, "variation_attribute") == true
                                  || GetOptionalBool(tags, "allow_variations") == true
                                  || GetOptionalBool(tags, "defines_picture") == true;

                    foreach (var tag in tags.EnumerateObject())
                    {
                        if (tag.Value.ValueKind == JsonValueKind.Null || tag.Value.ValueKind == JsonValueKind.Undefined)
                        {
                            continue;
                        }

                        parsedTags[tag.Name] = tag.Value.ValueKind switch
                        {
                            JsonValueKind.String => tag.Value.GetString() ?? string.Empty,
                            JsonValueKind.True => "true",
                            JsonValueKind.False => "false",
                            JsonValueKind.Number => tag.Value.GetRawText(),
                            _ => tag.Value.GetRawText()
                        };
                    }
                }

                var values = new List<MercadoLivreCategoryAttributeValueResponse>();
                if (node.TryGetProperty("values", out var valuesNode) && valuesNode.ValueKind == JsonValueKind.Array)
                {
                    foreach (var valueNode in valuesNode.EnumerateArray())
                    {
                        var valueId = GetOptionalString(valueNode, "id");
                        var valueName = GetOptionalString(valueNode, "name")
                                        ?? GetOptionalString(valueNode, "value_name");
                        if (string.IsNullOrWhiteSpace(valueId) && string.IsNullOrWhiteSpace(valueName))
                        {
                            continue;
                        }

                        values.Add(new MercadoLivreCategoryAttributeValueResponse
                        {
                            Id = valueId?.Trim() ?? string.Empty,
                            Name = valueName?.Trim() ?? string.Empty
                        });
                    }
                }

                attributes.Add(new MercadoLivreCategoryAttributeResponse
                {
                    Id = id.Trim(),
                    Name = name.Trim(),
                    Required = required,
                    Conditional = conditional,
                    IsVariation = isVariation,
                    ValueType = GetOptionalString(node, "value_type"),
                    Values = values,
                    Tags = parsedTags
                });
            }

            return (IReadOnlyList<MercadoLivreCategoryAttributeResponse>)attributes;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<MercadoLivreDomainDiscoverySuggestion>> SuggestCategoriesByDomainDiscoveryAsync(
        string siteId,
        string query,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithResilienceAsync(async ct =>
        {
            var safeSiteId = string.IsNullOrWhiteSpace(siteId) ? "MLB" : siteId.Trim().ToUpperInvariant();
            var safeQuery = (query ?? string.Empty).Trim();
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"/sites/{Uri.EscapeDataString(safeSiteId)}/domain_discovery/search?q={Uri.EscapeDataString(safeQuery)}&limit=8");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var items = new List<MercadoLivreDomainDiscoverySuggestion>();
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return (IReadOnlyList<MercadoLivreDomainDiscoverySuggestion>)items;
            }

            foreach (var node in doc.RootElement.EnumerateArray())
            {
                var categoryId = GetOptionalString(node, "category_id");
                var categoryName = GetOptionalString(node, "category_name");
                if (string.IsNullOrWhiteSpace(categoryId) || string.IsNullOrWhiteSpace(categoryName))
                {
                    continue;
                }

                var pathFromRoot =
                    GetOptionalString(node, "path_from_root") ??
                    GetOptionalString(node, "pathFromRoot") ??
                    categoryName;
                var score =
                    GetOptionalDecimal(node, "score") ??
                    GetOptionalDecimal(node, "probability") ??
                    GetOptionalDecimal(node, "ratio");

                items.Add(new MercadoLivreDomainDiscoverySuggestion
                {
                    CategoryId = categoryId.Trim(),
                    CategoryName = categoryName.Trim(),
                    DomainId = GetOptionalString(node, "domain_id")?.Trim(),
                    DomainName = GetOptionalString(node, "domain_name")?.Trim(),
                    Score = score,
                    PathFromRoot = string.IsNullOrWhiteSpace(pathFromRoot) ? categoryName.Trim() : pathFromRoot.Trim()
                });
            }

            return (IReadOnlyList<MercadoLivreDomainDiscoverySuggestion>)items;
        }, cancellationToken);
    }

    public async Task UpdateItemStockAsync(
        string itemId,
        int availableQuantity,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithResilienceAsync(async ct =>
        {
            var payload = JsonSerializer.Serialize(new
            {
                available_quantity = Math.Max(0, availableQuantity)
            });

            using var request = new HttpRequestMessage(HttpMethod.Put, $"/items/{Uri.EscapeDataString(itemId)}")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return true;
        }, cancellationToken);
    }

    public async Task UpdateVariationStockAsync(
        string itemId,
        string variationId,
        int availableQuantity,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithResilienceAsync(async ct =>
        {
            var payload = JsonSerializer.Serialize(new
            {
                available_quantity = Math.Max(0, availableQuantity)
            });

            // TODO: confirm the exact variation stock update path/payload against Mercado Livre docs.
            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                $"/items/{Uri.EscapeDataString(itemId)}/variations/{Uri.EscapeDataString(variationId)}")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return true;
        }, cancellationToken);
    }

    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        // /sites/MLB is a public endpoint that requires no authentication.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sites/MLB");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<T> ExecuteWithResilienceAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, _options.Resilience.RetryMaxAttempts);
        var baseDelayMs = Math.Max(50, _options.Resilience.RetryBaseDelayMs);
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ThrowIfCircuitOpen();
            try
            {
                var result = await operation(cancellationToken);
                RegisterSuccess();
                return result;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < maxAttempts)
            {
                lastException = ex;
                RegisterFailure();
                await Task.Delay(CalculateDelay(baseDelayMs, attempt), cancellationToken);
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (!IsBadRequestException(ex))
                {
                    RegisterFailure();
                }
                break;
            }
        }

        throw lastException ?? new InvalidOperationException("Mercado Livre request failed");
    }

    private static bool IsBadRequestException(Exception ex)
    {
        if (ex is HttpRequestException httpEx)
        {
            return httpEx.StatusCode == HttpStatusCode.BadRequest;
        }

        if (ex is MercadoLivreApiException mercadoLivreApiException)
        {
            return mercadoLivreApiException.StatusCode == HttpStatusCode.BadRequest;
        }

        return false;
    }

    private Task ExecuteWithResilienceAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        return ExecuteWithResilienceAsync(async ct =>
        {
            await operation(ct);
            return true;
        }, cancellationToken);
    }

    private void ThrowIfCircuitOpen()
    {
        lock (CircuitLock)
        {
            if (_circuitOpenUntil > DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException("ML_CIRCUIT_OPEN");
            }
        }
    }

    private void RegisterSuccess()
    {
        lock (CircuitLock)
        {
            _consecutiveTransientFailures = 0;
            _circuitOpenUntil = DateTimeOffset.MinValue;
        }
    }

    private void RegisterFailure()
    {
        lock (CircuitLock)
        {
            _consecutiveTransientFailures++;
            if (_consecutiveTransientFailures >= Math.Max(1, _options.Resilience.CircuitBreakerFailureThreshold))
            {
                _circuitOpenUntil = DateTimeOffset.UtcNow.AddSeconds(
                    Math.Max(1, _options.Resilience.CircuitBreakerDurationSeconds));
                _consecutiveTransientFailures = 0;
            }
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode)
    {
        if (statusCode == HttpStatusCode.TooManyRequests || statusCode == HttpStatusCode.RequestTimeout)
        {
            return true;
        }

        return (int)statusCode >= 500;
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is TaskCanceledException or TimeoutException)
        {
            return true;
        }

        if (ex is MercadoLivreApiException mlApiEx)
        {
            return !mlApiEx.StatusCode.HasValue || IsTransient(mlApiEx.StatusCode.Value);
        }

        if (ex is HttpRequestException httpEx)
        {
            return !httpEx.StatusCode.HasValue || IsTransient(httpEx.StatusCode.Value);
        }

        return false;
    }

    private static async Task EnsureSuccessOrThrowApiExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? rawBody = null;
        try
        {
            rawBody = response.Content == null
                ? null
                : await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            rawBody = null;
        }

        var (errorCode, errorMessage) = ParseMlError(rawBody);
        throw new MercadoLivreApiException(
            response.StatusCode,
            errorCode,
            errorMessage ?? response.ReasonPhrase ?? "Mercado Livre API request failed.",
            rawBody);
    }

    private static (string? errorCode, string? errorMessage) ParseMlError(string? rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;

            var errorCode = GetOptionalString(root, "error");
            var errorMessage = GetOptionalString(root, "message")
                               ?? GetOptionalString(root, "error_description");

            if (string.IsNullOrWhiteSpace(errorCode)
                && root.TryGetProperty("cause", out var causeElement)
                && causeElement.ValueKind == JsonValueKind.Array
                && causeElement.GetArrayLength() > 0)
            {
                var firstCause = causeElement[0];
                errorCode = GetOptionalString(firstCause, "code")
                            ?? GetOptionalString(firstCause, "error");
                errorMessage ??= GetOptionalString(firstCause, "message");
            }

            return (
                string.IsNullOrWhiteSpace(errorCode) ? null : errorCode.Trim(),
                string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage.Trim());
        }
        catch
        {
            return (null, rawBody.Trim());
        }
    }

    private static TimeSpan CalculateDelay(int baseDelayMs, int attempt)
    {
        var multiplier = Math.Pow(2, Math.Max(0, attempt - 1));
        var delayMs = (int)Math.Min(10000, baseDelayMs * multiplier);
        return TimeSpan.FromMilliseconds(delayMs);
    }

    private static string GetRequiredString(JsonElement root, string propertyName)
    {
        var value = GetOptionalString(root, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing required property '{propertyName}' in Mercado Livre response.");
        }

        return value;
    }

    private static int GetRequiredInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element))
        {
            throw new InvalidOperationException($"Missing required property '{propertyName}' in Mercado Livre response.");
        }

        return ParseInt(element);
    }

    private static string? GetOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static int ParseInt(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String &&
            int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static decimal? GetOptionalDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var parsed) => parsed,
            JsonValueKind.String when decimal.TryParse(
                value.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null
        };
    }

    private static int? GetOptionalInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null
        };
    }

    private static bool? GetOptionalBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static DateTimeOffset? TryParseDateTimeOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    private static string? ResolveCategoryPathFromRoot(JsonElement root, string? categoryNameFallback)
    {
        if (root.TryGetProperty("path_from_root", out var pathFromRootElement))
        {
            if (pathFromRootElement.ValueKind == JsonValueKind.String)
            {
                var raw = pathFromRootElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    return raw;
                }
            }

            if (pathFromRootElement.ValueKind == JsonValueKind.Array)
            {
                var segments = new List<string>();
                foreach (var segment in pathFromRootElement.EnumerateArray())
                {
                    var name = segment.ValueKind switch
                    {
                        JsonValueKind.String => segment.GetString(),
                        JsonValueKind.Object => GetOptionalString(segment, "name"),
                        _ => null
                    };

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        segments.Add(name.Trim());
                    }
                }

                if (segments.Count > 0)
                {
                    return string.Join(" > ", segments);
                }
            }
        }

        if (root.TryGetProperty("pathFromRoot", out var pathFromRootCamel))
        {
            var raw = pathFromRootCamel.ValueKind == JsonValueKind.String
                ? pathFromRootCamel.GetString()?.Trim()
                : null;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return raw;
            }
        }

        return string.IsNullOrWhiteSpace(categoryNameFallback) ? null : categoryNameFallback;
    }

    private static string? ResolveOrderSellerId(JsonElement root)
    {
        if (root.TryGetProperty("seller", out var sellerElement))
        {
            return GetOptionalString(sellerElement, "id");
        }

        if (root.TryGetProperty("seller_id", out var sellerIdElement))
        {
            return GetOptionalString(root, "seller_id");
        }

        return null;
    }

    private static string? ResolveShipmentSellerId(JsonElement root)
    {
        if (root.TryGetProperty("seller_id", out var sellerIdElement))
        {
            return GetOptionalString(root, "seller_id");
        }

        if (root.TryGetProperty("sender_id", out var senderIdElement))
        {
            return GetOptionalString(root, "sender_id");
        }

        if (root.TryGetProperty("sender", out var senderElement))
        {
            return GetOptionalString(senderElement, "id");
        }

        return null;
    }

    private static string? ExtractVariationSku(JsonElement variation)
    {
        if (!variation.TryGetProperty("attributes", out var attributesElement)
            || attributesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var attribute in attributesElement.EnumerateArray())
        {
            var id = GetOptionalString(attribute, "id");
            if (!string.Equals(id, "SELLER_SKU", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = GetOptionalString(attribute, "value_name")
                        ?? GetOptionalString(attribute, "value_id");
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string ComputeSha256(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? TryExtractLabelUrl(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (TryFindString(root, "url", out var directUrl))
            {
                return directUrl;
            }

            if (TryFindString(root, "file", out var fileUrl))
            {
                return fileUrl;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryFindString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = GetOptionalString(element, prop.Name);
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return true;
                        }
                    }

                    if (TryFindString(prop.Value, propertyName, out value))
                    {
                        return true;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    if (TryFindString(child, propertyName, out value))
                    {
                        return true;
                    }
                }

                break;
        }

        return false;
    }
}
