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
        string? shopCipher = null,
        string? pageToken = null,
        CancellationToken cancellationToken = default);

    Task<TikTokShopApiResponse<TikTokShopOrderDetailData>> GetOrderDetailAsync(
        string accessToken,
        string appKey,
        string appSecret,
        string[] orderIds,
        string? shopCipher = null,
        string? shopId = null,
        CancellationToken cancellationToken = default);

    Task<TikTokShopApiResponse<TikTokShopPackageSearchData>> SearchPackagesAsync(
        string accessToken,
        string appKey,
        string appSecret,
        DateTimeOffset from,
        DateTimeOffset to,
        string? shopCipher = null,
        string? cursor = null,
        CancellationToken cancellationToken = default);

    Task<TikTokShopApiResponse<TikTokShopPackageDetail>> GetPackageDetailAsync(
        string accessToken,
        string appKey,
        string appSecret,
        string packageId,
        string? shopCipher = null,
        CancellationToken cancellationToken = default);

    Task<TikTokShopApiResponse<TikTokShopShippingDocumentData>> GetPackageShippingDocumentAsync(
        string accessToken,
        string appKey,
        string appSecret,
        string packageId,
        string? shopCipher = null,
        int documentType = 1,
        int documentSize = 0,
        CancellationToken cancellationToken = default);

    Task PingAsync(
        string accessToken,
        string appKey,
        string appSecret,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TikTokShopAuthorizedShop>> GetAuthorizedShopsAsync(
        string accessToken,
        string appKey,
        string appSecret,
        CancellationToken cancellationToken = default);

    Task<TikTokShopApiResponse<TikTokShopImageUploadData>> UploadImageFromUrlAsync(
        string imageUrl,
        string accessToken,
        string appKey,
        string appSecret,
        string? shopCipher = null,
        CancellationToken cancellationToken = default);

    Task<TikTokShopApiResponse<TikTokShopCreateProductData>> CreateProductAsync(
        TikTokShopCreateProductRequest request,
        string accessToken,
        string appKey,
        string appSecret,
        string? shopCipher = null,
        CancellationToken cancellationToken = default);

    Task<TikTokShopApiResponse<TikTokShopProductListData>> GetProductsAsync(
        string accessToken,
        string appKey,
        string appSecret,
        string? shopCipher = null,
        int pageSize = 20,
        string? pageToken = null,
        CancellationToken cancellationToken = default);

    Task<TikTokShopApiResponse<TikTokShopCategoryData>> GetCategoriesAsync(
        string accessToken,
        string appKey,
        string appSecret,
        string? shopCipher = null,
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

public sealed class TikTokShopAuthorizedShop
{
    public string ShopId { get; set; } = string.Empty;

    public string ShopCipher { get; set; } = string.Empty;

    public string? ShopName { get; set; }

    public string? SellerName { get; set; }

    public string? SellerBaseRegion { get; set; }
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
    private string? _alternateId;

    [JsonPropertyName("order_id")]
    public string OrderId { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string? AlternateId
    {
        get => _alternateId;
        set
        {
            _alternateId = value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                OrderId = value;
            }
        }
    }

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
    private List<TikTokShopOrderDetail>? _alternateOrders;

    [JsonPropertyName("orders")]
    public List<TikTokShopOrderDetail> Orders { get; set; } = [];

    [JsonPropertyName("order_list")]
    public List<TikTokShopOrderDetail>? AlternateOrders
    {
        get => _alternateOrders;
        set
        {
            _alternateOrders = value;
            if (value is { Count: > 0 })
            {
                Orders = value;
            }
        }
    }
}

public sealed class TikTokShopOrderDetail
{
    private string? _alternateId;
    private List<TikTokShopOrderLineItem>? _alternateLineItems;
    private List<TikTokShopOrderLineItem>? _itemList;

    [JsonPropertyName("order_id")]
    public string OrderId { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string? AlternateId
    {
        get => _alternateId;
        set
        {
            _alternateId = value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                OrderId = value;
            }
        }
    }

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

    [JsonPropertyName("line_item_list")]
    public List<TikTokShopOrderLineItem>? AlternateLineItems
    {
        get => _alternateLineItems;
        set
        {
            _alternateLineItems = value;
            if (value is { Count: > 0 })
            {
                LineItems = value;
            }
        }
    }

    [JsonPropertyName("item_list")]
    public List<TikTokShopOrderLineItem>? ItemList
    {
        get => _itemList;
        set
        {
            _itemList = value;
            if (value is { Count: > 0 })
            {
                LineItems = value;
            }
        }
    }

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

    [JsonPropertyName("seller_sku")]
    public string? SellerSku { get; set; }

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

public sealed class TikTokShopPackageSearchData
{
    [JsonPropertyName("more")]
    public bool More { get; set; }

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("package_list")]
    public List<TikTokShopPackageSummary> PackageList { get; set; } = [];
}

public sealed class TikTokShopPackageSummary
{
    [JsonPropertyName("package_id")]
    public string PackageId { get; set; } = string.Empty;

    [JsonPropertyName("package_status")]
    public int PackageStatus { get; set; }

    [JsonPropertyName("create_time")]
    public long CreateTime { get; set; }

    [JsonPropertyName("update_time")]
    public long UpdateTime { get; set; }
}

public sealed class TikTokShopPackageDetail
{
    [JsonPropertyName("package_id")]
    public string PackageId { get; set; } = string.Empty;

    [JsonPropertyName("package_status")]
    public int PackageStatus { get; set; }

    [JsonPropertyName("package_freeze_status")]
    public int PackageFreezeStatus { get; set; }

    [JsonPropertyName("delivery_option")]
    public int DeliveryOption { get; set; }

    [JsonPropertyName("shipping_provider")]
    public string? ShippingProvider { get; set; }

    [JsonPropertyName("shipping_provider_id")]
    public string? ShippingProviderId { get; set; }

    [JsonPropertyName("tracking_number")]
    public string? TrackingNumber { get; set; }

    [JsonPropertyName("pick_up_type")]
    public int PickUpType { get; set; }

    [JsonPropertyName("pick_up_start_time")]
    public long? PickUpStartTime { get; set; }

    [JsonPropertyName("pick_up_end_time")]
    public long? PickUpEndTime { get; set; }

    [JsonPropertyName("create_time")]
    public long CreateTime { get; set; }

    [JsonPropertyName("update_time")]
    public long UpdateTime { get; set; }

    [JsonPropertyName("order_info_list")]
    public List<TikTokShopPackageOrderInfo> OrderInfoList { get; set; } = [];
}

public sealed class TikTokShopPackageOrderInfo
{
    [JsonPropertyName("order_id")]
    public string OrderId { get; set; } = string.Empty;

    [JsonPropertyName("sku_list")]
    public List<TikTokShopPackageSkuInfo> SkuList { get; set; } = [];
}

public sealed class TikTokShopPackageSkuInfo
{
    [JsonPropertyName("quantity")]
    public string? Quantity { get; set; }

    [JsonPropertyName("sku_id")]
    public string? SkuId { get; set; }

    [JsonPropertyName("sku_image")]
    public string? SkuImage { get; set; }

    [JsonPropertyName("sku_name")]
    public string? SkuName { get; set; }
}

public sealed class TikTokShopShippingDocumentData
{
    [JsonPropertyName("doc_url")]
    public string? DocUrl { get; set; }
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

    [JsonPropertyName("categories")]
    public List<TikTokShopCategory>? Categories
    {
        set
        {
            if (value is { Count: > 0 })
            {
                CategoryList = value;
            }
        }
    }
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
