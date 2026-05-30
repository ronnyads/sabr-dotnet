using System.Text.Json.Serialization;

namespace Phub.Application.Abstractions;

public interface IShopeeApiClient
{
    Task<ShopeeTokenResponse> ExchangeCodeAsync(
        string code,
        long? shopId = null,
        long? mainAccountId = null,
        CancellationToken cancellationToken = default);

    Task<ShopeeRefreshTokenResponse> RefreshAccessTokenAsync(
        string refreshToken,
        long shopId,
        CancellationToken cancellationToken = default);

    Task<ShopeeApiEnvelope<ShopeeShopInfoResponse>> GetShopInfoAsync(
        string accessToken,
        long shopId,
        CancellationToken cancellationToken = default);

    Task<ShopeeApiEnvelope<ShopeeOrderListResponse>> GetOrderListAsync(
        string accessToken,
        long shopId,
        long timeFrom,
        long timeTo,
        string? cursor = null,
        CancellationToken cancellationToken = default);

    Task<ShopeeApiEnvelope<ShopeeOrderDetailResponse>> GetOrderDetailAsync(
        string accessToken,
        long shopId,
        IReadOnlyCollection<string> orderSnList,
        CancellationToken cancellationToken = default);

    Task PingAsync(CancellationToken cancellationToken = default);
}

public sealed class ShopeeApiEnvelope<TResponse>
{
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("warning")]
    public string? Warning { get; set; }

    [JsonPropertyName("response")]
    public TResponse? Response { get; set; }
}

public sealed class ShopeeTokenResponse
{
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("expire_in")]
    public int ExpireIn { get; set; }

    [JsonPropertyName("shop_id")]
    public long? ShopId { get; set; }

    [JsonPropertyName("main_account_id")]
    public long? MainAccountId { get; set; }

    [JsonPropertyName("shop_id_list")]
    public List<long> ShopIdList { get; set; } = new();

    [JsonPropertyName("merchant_id_list")]
    public List<long> MerchantIdList { get; set; } = new();

    [JsonPropertyName("supplier_id_list")]
    public List<long> SupplierIdList { get; set; } = new();

    [JsonPropertyName("user_id_list")]
    public List<long> UserIdList { get; set; } = new();
}

public sealed class ShopeeRefreshTokenResponse
{
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("expire_in")]
    public int ExpireIn { get; set; }

    [JsonPropertyName("shop_id")]
    public long? ShopId { get; set; }
}

public sealed class ShopeeShopInfoResponse
{
    [JsonPropertyName("shop_name")]
    public string ShopName { get; set; } = string.Empty;

    [JsonPropertyName("region")]
    public string Region { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("auth_time")]
    public long AuthTime { get; set; }

    [JsonPropertyName("expire_time")]
    public long ExpireTime { get; set; }

    [JsonPropertyName("merchant_id")]
    public long? MerchantId { get; set; }
}

public sealed class ShopeeOrderListResponse
{
    [JsonPropertyName("more")]
    public bool More { get; set; }

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("order_list")]
    public List<ShopeeOrderListItem> OrderList { get; set; } = new();
}

public sealed class ShopeeOrderListItem
{
    [JsonPropertyName("order_sn")]
    public string OrderSn { get; set; } = string.Empty;

    [JsonPropertyName("order_status")]
    public string? OrderStatus { get; set; }

    [JsonPropertyName("booking_sn")]
    public string? BookingSn { get; set; }
}

public sealed class ShopeeOrderDetailResponse
{
    [JsonPropertyName("order_list")]
    public List<ShopeeOrderDetail> OrderList { get; set; } = new();
}

public sealed class ShopeeOrderDetail
{
    [JsonPropertyName("order_sn")]
    public string OrderSn { get; set; } = string.Empty;

    [JsonPropertyName("order_status")]
    public string OrderStatus { get; set; } = string.Empty;

    [JsonPropertyName("shipping_carrier")]
    public string? ShippingCarrier { get; set; }

    [JsonPropertyName("booking_sn")]
    public string? BookingSn { get; set; }

    [JsonPropertyName("pay_time")]
    public long? PayTime { get; set; }

    [JsonPropertyName("create_time")]
    public long CreateTime { get; set; }

    [JsonPropertyName("update_time")]
    public long UpdateTime { get; set; }

    [JsonPropertyName("ship_by_date")]
    public long? ShipByDate { get; set; }

    [JsonPropertyName("item_list")]
    public List<ShopeeOrderDetailItem> ItemList { get; set; } = new();

    [JsonPropertyName("package_list")]
    public List<ShopeeOrderPackage> PackageList { get; set; } = new();
}

public sealed class ShopeeOrderDetailItem
{
    [JsonPropertyName("item_id")]
    public long ItemId { get; set; }

    [JsonPropertyName("item_name")]
    public string ItemName { get; set; } = string.Empty;

    [JsonPropertyName("item_sku")]
    public string? ItemSku { get; set; }

    [JsonPropertyName("model_id")]
    public long? ModelId { get; set; }

    [JsonPropertyName("model_name")]
    public string? ModelName { get; set; }

    [JsonPropertyName("model_sku")]
    public string? ModelSku { get; set; }

    [JsonPropertyName("model_quantity_purchased")]
    public int QuantityPurchased { get; set; }

    [JsonPropertyName("order_item_id")]
    public long? OrderItemId { get; set; }
}

public sealed class ShopeeOrderPackage
{
    [JsonPropertyName("package_number")]
    public string? PackageNumber { get; set; }

    [JsonPropertyName("logistics_status")]
    public string? LogisticsStatus { get; set; }

    [JsonPropertyName("logistics_channel_id")]
    public long? LogisticsChannelId { get; set; }
}
