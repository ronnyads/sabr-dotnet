using System.Text.Json.Serialization;

namespace Phub.Application.Abstractions;

public interface ITikTokShopApiClient
{
    Task<TikTokShopTokenEnvelope> ExchangeCodeAsync(
        string appKey,
        string appSecret,
        string authCode,
        CancellationToken cancellationToken = default);

    Task<TikTokShopTokenEnvelope> RefreshTokenAsync(
        string appKey,
        string appSecret,
        string refreshToken,
        CancellationToken cancellationToken = default);

    Task<TikTokShopApiResponse<TikTokShopOrderSearchData>> SearchOrdersAsync(
        string accessToken,
        string appKey,
        string appSecret,
        DateTimeOffset from,
        DateTimeOffset to,
        string? pageToken = null,
        CancellationToken cancellationToken = default);

    Task<TikTokShopApiResponse<TikTokShopOrderDetailData>> GetOrderDetailAsync(
        string accessToken,
        string appKey,
        string appSecret,
        string[] orderIds,
        CancellationToken cancellationToken = default);

    Task PingAsync(
        string accessToken,
        string appKey,
        string appSecret,
        CancellationToken cancellationToken = default);

    Task<TikTokShopApiResponse<TikTokShopImageUploadData>> UploadImageFromUrlAsync(
        string imageUrl,
        string accessToken,
        string appKey,
        string appSecret,
        CancellationToken cancellationToken = default);

    Task<TikTokShopApiResponse<TikTokShopCreateProductData>> CreateProductAsync(
        TikTokShopCreateProductRequest request,
        string accessToken,
        string appKey,
        string appSecret,
        CancellationToken cancellationToken = default);

    Task<TikTokShopApiResponse<TikTokShopProductListData>> GetProductsAsync(
        string accessToken,
        string appKey,
        string appSecret,
        int pageSize = 20,
        string? pageToken = null,
        CancellationToken cancellationToken = default);

    Task<TikTokShopApiResponse<TikTokShopCategoryData>> GetCategoriesAsync(
        string accessToken,
        string appKey,
        string appSecret,
        CancellationToken cancellationToken = default);
}

public sealed class TikTokShopTokenEnvelope
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("request_id")]
    public string? RequestId { get; set; }

    [JsonPropertyName("data")]
    public TikTokShopTokenPayload? Data { get; set; }
}

public sealed class TikTokShopTokenPayload
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("access_token_expire_in")]
    public long? AccessTokenExpireIn { get; set; }

    [JsonPropertyName("refresh_token_expire_in")]
    public long? RefreshTokenExpireIn { get; set; }

    [JsonPropertyName("seller_name")]
    public string? SellerName { get; set; }

    [JsonPropertyName("seller_base_region")]
    public string? SellerBaseRegion { get; set; }

    [JsonPropertyName("open_id")]
    public string? OpenId { get; set; }

    [JsonPropertyName("shop_cipher")]
    public string? ShopCipher { get; set; }

    [JsonPropertyName("shop_id")]
    public string? ShopId { get; set; }

    [JsonPropertyName("expires_in")]
    public long? ExpiresIn { get; set; }
}

// Generic API response wrapper
public sealed class TikTokShopApiResponse<T>
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("request_id")]
    public string? RequestId { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    public bool IsSuccess => Code == 0;
}

// Order search
public sealed class TikTokShopOrderSearchData
{
    [JsonPropertyName("orders")]
    public List<TikTokShopOrderSummary> Orders { get; set; } = [];

    [JsonPropertyName("next_page_token")]
    public string? NextPageToken { get; set; }

    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }
}

public sealed class TikTokShopOrderSummary
{
    [JsonPropertyName("order_id")]
    public string OrderId { get; set; } = string.Empty;

    [JsonPropertyName("order_status")]
    public string OrderStatus { get; set; } = string.Empty;

    [JsonPropertyName("create_time")]
    public long CreateTime { get; set; }

    [JsonPropertyName("update_time")]
    public long UpdateTime { get; set; }
}

// Order detail
public sealed class TikTokShopOrderDetailData
{
    [JsonPropertyName("orders")]
    public List<TikTokShopOrderDetail> Orders { get; set; } = [];
}

public sealed class TikTokShopOrderDetail
{
    [JsonPropertyName("order_id")]
    public string OrderId { get; set; } = string.Empty;

    [JsonPropertyName("order_status")]
    public string OrderStatus { get; set; } = string.Empty;

    [JsonPropertyName("create_time")]
    public long CreateTime { get; set; }

    [JsonPropertyName("paid_time")]
    public long? PaidTime { get; set; }

    [JsonPropertyName("update_time")]
    public long UpdateTime { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("shop_id")]
    public string? ShopId { get; set; }

    [JsonPropertyName("line_items")]
    public List<TikTokShopOrderLineItem> LineItems { get; set; } = [];

    [JsonPropertyName("payment")]
    public TikTokShopOrderPayment? Payment { get; set; }

    [JsonPropertyName("recipient_address")]
    public TikTokShopRecipientAddress? RecipientAddress { get; set; }
}

public sealed class TikTokShopOrderLineItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("product_id")]
    public string? ProductId { get; set; }

    [JsonPropertyName("sku_id")]
    public string? SkuId { get; set; }

    [JsonPropertyName("product_name")]
    public string? ProductName { get; set; }

    [JsonPropertyName("sku_name")]
    public string? SkuName { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("sale_price")]
    public string? SalePrice { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("item_tax")]
    public string? ItemTax { get; set; }
}

public sealed class TikTokShopOrderPayment
{
    [JsonPropertyName("total_amount")]
    public string? TotalAmount { get; set; }

    [JsonPropertyName("sub_total")]
    public string? SubTotal { get; set; }

    [JsonPropertyName("shipping_fee")]
    public string? ShippingFee { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }
}

public sealed class TikTokShopRecipientAddress
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("region_code")]
    public string? RegionCode { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("district")]
    public string? District { get; set; }

    [JsonPropertyName("address_line1")]
    public string? AddressLine1 { get; set; }

    [JsonPropertyName("zipcode")]
    public string? Zipcode { get; set; }
}

// Image upload
public sealed class TikTokShopImageUploadData
{
    [JsonPropertyName("img_id")]
    public string ImgId { get; set; } = string.Empty;

    [JsonPropertyName("img_url")]
    public string ImgUrl { get; set; } = string.Empty;
}

// Create product request/response
public sealed class TikTokShopCreateProductRequest
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("category_id")]
    public string CategoryId { get; set; } = string.Empty;

    [JsonPropertyName("images")]
    public List<TikTokShopProductImageItem> Images { get; set; } = [];

    [JsonPropertyName("skus")]
    public List<TikTokShopProductSkuItem> Skus { get; set; } = [];

    [JsonPropertyName("package_weight")]
    public TikTokShopPackageWeight? PackageWeight { get; set; }
}

public sealed class TikTokShopProductImageItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}

public sealed class TikTokShopProductSkuItem
{
    [JsonPropertyName("seller_sku")]
    public string SellerSku { get; set; } = string.Empty;

    [JsonPropertyName("price")]
    public TikTokShopSkuPrice? Price { get; set; }

    [JsonPropertyName("inventory")]
    public List<TikTokShopSkuInventory> Inventory { get; set; } = [];
}

public sealed class TikTokShopSkuPrice
{
    [JsonPropertyName("amount")]
    public string Amount { get; set; } = string.Empty;

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "BRL";
}

public sealed class TikTokShopSkuInventory
{
    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }
}

public sealed class TikTokShopPackageWeight
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "KILOGRAM";
}

public sealed class TikTokShopCreateProductData
{
    [JsonPropertyName("product_id")]
    public string ProductId { get; set; } = string.Empty;

    [JsonPropertyName("skus")]
    public List<TikTokShopCreatedSkuItem> Skus { get; set; } = [];
}

public sealed class TikTokShopCreatedSkuItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("seller_sku")]
    public string SellerSku { get; set; } = string.Empty;
}

// Product list
public sealed class TikTokShopProductListData
{
    [JsonPropertyName("products")]
    public List<TikTokShopProductSummary> Products { get; set; } = [];

    [JsonPropertyName("next_page_token")]
    public string? NextPageToken { get; set; }

    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }
}

public sealed class TikTokShopProductSummary
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("create_time")]
    public long CreateTime { get; set; }

    [JsonPropertyName("skus")]
    public List<TikTokShopCreatedSkuItem> Skus { get; set; } = [];
}

// Categories
public sealed class TikTokShopCategoryData
{
    [JsonPropertyName("category_list")]
    public List<TikTokShopCategory> CategoryList { get; set; } = [];
}

public sealed class TikTokShopCategory
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("parent_id")]
    public string ParentId { get; set; } = string.Empty;

    [JsonPropertyName("local_name")]
    public string LocalName { get; set; } = string.Empty;

    [JsonPropertyName("is_leaf")]
    public bool IsLeaf { get; set; }

    [JsonPropertyName("permission_statuses")]
    public List<string> PermissionStatuses { get; set; } = [];
}
