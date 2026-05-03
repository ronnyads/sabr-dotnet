using Phub.Domain.Enums;

namespace Phub.Application.Models;

// ── Order Cancel / Refund ────────────────────────────────────────────────────

public sealed class OrderCancelRequest
{
    public string? Reason { get; set; }
}

public sealed class OrderRefundRequest
{
    public string? Reason { get; set; }
}

public sealed class OrderActionResult
{
    public Guid OrderId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

// ── Admin Order List ─────────────────────────────────────────────────────────

public class AdminOrderListItemResult
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public string? ClientName { get; set; }
    public MarketplaceProvider Provider { get; set; }
    public string SellerId { get; set; } = string.Empty;
    public string MlOrderId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset? SabrPaymentConfirmedAt { get; set; }
    public string? ShipmentId { get; set; }
    public string? ShippingMode { get; set; }
    public string? LogisticType { get; set; }
    public DateTimeOffset? ShipByDeadlineAt { get; set; }
    public bool HasUnmappedItems { get; set; }
    public int TotalItems { get; set; }
    public bool HasLabel { get; set; }
    public string? RiskFlagsJson { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}

public sealed class AdminOrderDetailResult : AdminOrderListItemResult
{
    public List<AdminOrderItemResult> Items { get; set; } = new();
}

public sealed class AdminOrderItemResult
{
    public Guid Id { get; set; }
    public string MlItemId { get; set; } = string.Empty;
    public string? MlVariationId { get; set; }
    public string? SabrVariantSku { get; set; }
    public string? ProductName { get; set; }
    public int Quantity { get; set; }
    public int ReservedQuantity { get; set; }
    public string MappingState { get; set; } = string.Empty;
}

public sealed class AdminFulfillmentOrderResult
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public string? ClientName { get; set; }
    public string MlOrderId { get; set; } = string.Empty;
    public string SellerId { get; set; } = string.Empty;
    public string? ShipmentId { get; set; }
    public string? ShippingMode { get; set; }
    public string? LogisticType { get; set; }
    public DateTimeOffset? ShipByDeadlineAt { get; set; }
    public bool IsUrgent { get; set; }
    public bool HasLabel { get; set; }
    public int TotalItems { get; set; }
    public DateTimeOffset SabrPaymentConfirmedAt { get; set; }
}

public static class MarketplaceMappingStates
{
    public const string Mapped = "MAPPED";
    public const string Unmapped = "UNMAPPED";
}

public sealed class MercadoLivreTokenResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public int ExpiresInSeconds { get; set; }
}

public sealed class MercadoLivreUserMeResponse
{
    public string SellerId { get; set; } = string.Empty;
    public string? Nickname { get; set; }
}

public sealed class MercadoLivreOrderItemDetails
{
    public string MlItemId { get; set; } = string.Empty;
    public string? MlVariationId { get; set; }
    public int Quantity { get; set; }
    public string RawJson { get; set; } = "{}";
}

public sealed class MercadoLivreOrderDetails
{
    public string MlOrderId { get; set; } = string.Empty;
    public string? SellerId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset? PaidAt { get; set; }
    public string? ShipmentId { get; set; }
    public string? ShippingMode { get; set; }
    public string? LogisticType { get; set; }
    public DateTimeOffset? ShipByDeadlineAt { get; set; }
    public string RawJson { get; set; } = "{}";
    public List<MercadoLivreOrderItemDetails> Items { get; set; } = new();
}

public sealed class MercadoLivreShipmentDetails
{
    public string? ShipmentId { get; set; }
    public string? SellerId { get; set; }
    public string? Status { get; set; }
    public string? Substatus { get; set; }
    public string? ShippingMode { get; set; }
    public string? LogisticType { get; set; }
    public string? TrackingNumber { get; set; }
    public string? TrackingMethod { get; set; }
    public string? TrackingUrl { get; set; }
    public DateTimeOffset? ShippedAt { get; set; }
    public DateTimeOffset? ShipByDeadlineAt { get; set; }
    public string RawJson { get; set; } = "{}";
}

public sealed class MercadoLivreShipmentLabelResult
{
    public string ShipmentId { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/pdf";
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class MarketplaceShipmentLabelDownloadResult
{
    public string ShipmentId { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/pdf";
    public string FileName { get; set; } = string.Empty;
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public string? SourceUrl { get; set; }
    public string? Sha256 { get; set; }
}

public sealed class MabangLabelDispatchRequest
{
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public string SellerId { get; set; } = string.Empty;
    public string ShipmentId { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/pdf";
    public string LabelSha256 { get; set; } = string.Empty;
    public string LabelBase64 { get; set; } = string.Empty;
}

public sealed class MercadoLivreConnectUrlRequest
{
    public string? ReturnUrl { get; set; }
}

public sealed class MercadoLivreDisconnectRequest
{
    public string? SellerId { get; set; }
}

public sealed class MercadoLivreCallbackRequest
{
    public string? Code { get; set; }
    public string? State { get; set; }
}

public sealed class MercadoLivreSyncNowRequest
{
    public string? SellerId { get; set; }
}

public sealed class MercadoLivreCreateMappingRequest
{
    public string SellerId { get; set; } = string.Empty;
    public string MlItemId { get; set; } = string.Empty;
    public string? MlVariationId { get; set; }
    public string SabrVariantSku { get; set; } = string.Empty;
}

public sealed class MarketplaceMarkPaidRequest
{
    public bool Force { get; set; }
}

public sealed class MercadoLivreConnectUrlResult
{
    public string Url { get; set; } = string.Empty;
}

public sealed class MercadoLivreConnectionStatusResult
{
    public Guid IntegrationId { get; set; }
    public string SellerId { get; set; } = string.Empty;
    public string? Nickname { get; set; }
    public DateTimeOffset TokenExpiresAt { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
}

public sealed class MercadoLivreIntegrationStatusResult
{
    public bool Connected { get; set; }
    public List<MercadoLivreConnectionStatusResult> Connections { get; set; } = new();
    public int MappingsCount { get; set; }
    public int OrdersCount { get; set; }
    public int UnmappedItemsCount { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
}

public sealed class MercadoLivreListingMapResult
{
    public Guid Id { get; set; }
    public Guid? IntegrationId { get; set; }
    public string SellerId { get; set; } = string.Empty;
    public string MlItemId { get; set; } = string.Empty;
    public string? MlVariationId { get; set; }
    public string SabrVariantSku { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class MercadoLivreSyncNowResult
{
    public int OrdersUpserted { get; set; }
    public int ItemsUpserted { get; set; }
    public int ReservationsCreated { get; set; }
}

public sealed class MarketplaceOrderListItemResult
{
    public Guid Id { get; set; }
    public MarketplaceProvider Provider { get; set; }
    public string SellerId { get; set; } = string.Empty;
    public string MlOrderId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset? SabrPaymentConfirmedAt { get; set; }
    public string? ShippingMode { get; set; }
    public string? LogisticType { get; set; }
    public DateTimeOffset? ShipByDeadlineAt { get; set; }
    public bool HasUnmappedItems { get; set; }
    public int TotalItems { get; set; }
    public int ReservedItems { get; set; }
    public string? RiskFlagsJson { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}

public sealed class MarketplaceMarkPaidResult
{
    public Guid OrderId { get; set; }
    public bool AlreadyPaid { get; set; }
    public DateTimeOffset? SabrPaymentConfirmedAt { get; set; }
    public string? RiskFlagsJson { get; set; }
}

public sealed class MarketplacePaymentConfirmationRequiredResult
{
    public DateTimeOffset? ShipByDeadlineAt { get; set; }
    public string CutoffLocalTime { get; set; } = string.Empty;
    public string NowLocal { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class MarketplaceMarkPaidExecutionResult
{
    public bool ConfirmationRequired { get; set; }
    public MarketplacePaymentConfirmationRequiredResult? Confirmation { get; set; }
    public MarketplaceMarkPaidResult? Result { get; set; }
}

public static class MarketplaceEventStatuses
{
    public const string Pending = "PENDING";
    public const string Processing = "PROCESSING";
    public const string Processed = "PROCESSED";
    public const string Failed = "FAILED";
    public const string DeadLetter = "DEAD_LETTER";
}

public static class MarketplaceOrderStatuses
{
    public const string PendingPayment   = "pending_payment";
    public const string Paid             = "paid";
    public const string PaymentConfirmed = "payment_confirmed";
    public const string LabelGenerated   = "label_generated";
    public const string Dispatched       = "dispatched";
    public const string Delivered        = "delivered";
    public const string Cancelled        = "cancelled";
    public const string RefundRequested  = "refund_requested";
    public const string Refunded         = "refunded";

    public static readonly IReadOnlySet<string> CancellableStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        PendingPayment, Paid, PaymentConfirmed, LabelGenerated, RefundRequested
    };

    public static readonly IReadOnlySet<string> RefundableStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        Paid, PaymentConfirmed, LabelGenerated, Dispatched
    };
}

public static class MarketplaceEventTopics
{
    public const string WebhookOrders = "orders_v2";
    public const string WebhookShipments = "shipments";
    public const string WebhookPayments = "payments";
    public const string MabangLabelDispatch = "mabang.label.dispatch";

    public const string AuditOrderCreated       = "audit.order.created";
    public const string AuditOrderPaid          = "audit.order.paid";
    public const string AuditOrderCancelled     = "audit.order.cancelled";
    public const string AuditOrderDispatched    = "audit.order.dispatched";
    public const string AuditOrderRefundRequest = "audit.order.refund_requested";
    public const string AuditOrderRefunded      = "audit.order.refunded";
    public const string AuditShipmentShipped    = "audit.shipment.shipped";
    public const string AuditLabelGenerated     = "audit.label.generated";
}

public sealed class MercadoLivreWebhookPayload
{
    public string? Id { get; set; }
    public string? Topic { get; set; }
    public string? Resource { get; set; }
    public string? UserId { get; set; }
}

public sealed class MercadoLivreWebhookIngestResult
{
    public bool Accepted { get; set; }
    public bool Duplicate { get; set; }
    public string? EventId { get; set; }
}

public sealed class MercadoLivrePublishValidateRequest
{
    public Guid? CatalogId { get; set; }
    public Guid? PlanId { get; set; }
    public List<string>? SabrVariantSkus { get; set; }
}

public sealed class MercadoLivrePublishValidationItemResult
{
    public string SabrVariantSku { get; set; } = string.Empty;
    public bool Eligible { get; set; }
    public List<string> Reasons { get; set; } = new();
}

public sealed class MercadoLivrePublishValidateResult
{
    public int Total { get; set; }
    public int Eligible { get; set; }
    public int Ineligible { get; set; }
    public List<MercadoLivrePublishValidationItemResult> Items { get; set; } = new();
}

public sealed class MercadoLivrePublishRequest
{
    public string? SellerId { get; set; }
    public Guid? CatalogId { get; set; }
    public Guid? PlanId { get; set; }
    public List<string>? SabrVariantSkus { get; set; }
}

public sealed class MercadoLivrePublishItemResult
{
    public string SellerId { get; set; } = string.Empty;
    public string SabrVariantSku { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? MlItemId { get; set; }
    public string? MlVariationId { get; set; }
    public List<string> Reasons { get; set; } = new();
    public string? Message { get; set; }
}

public sealed class MercadoLivrePublishResult
{
    public int Total { get; set; }
    public int Published { get; set; }
    public int AlreadyMapped { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<MercadoLivrePublishItemResult> Items { get; set; } = new();
}

public sealed class MercadoLivreListListingsResult
{
    public int Total { get; set; }
    public List<MercadoLivreListingItemResult> Items { get; set; } = new();
}

public sealed class MercadoLivreListingItemResult
{
    public Guid Id { get; set; }
    public Guid? IntegrationId { get; set; }
    public string SellerId { get; set; } = string.Empty;
    public string MlItemId { get; set; } = string.Empty;
    public string? MlVariationId { get; set; }
    public string SabrVariantSku { get; set; } = string.Empty;
    public string? ProductName { get; set; }
    public long CatalogPriceCents { get; set; }
    public int PhysicalStock { get; set; }
    public int ReservedStock { get; set; }
    public int AvailableStock { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class MercadoLivreCreateItemRequest
{
    public string Title { get; set; } = string.Empty;
    public string? CategoryId { get; set; }
    public decimal Price { get; set; }
    public int AvailableQuantity { get; set; }
    public string CurrencyId { get; set; } = "BRL";
    public string ListingTypeId { get; set; } = "gold_special";
    public string BuyingMode { get; set; } = "buy_it_now";
    public string Condition { get; set; } = "new";
    public string? Description { get; set; }
    public List<string> PictureUrls { get; set; } = new();
    public List<MercadoLivreCreateItemAttributeRequest> Attributes { get; set; } = new();
    public string SellerCustomField { get; set; } = string.Empty;
    public string SabrVariantSku { get; set; } = string.Empty;
    public List<MercadoLivreCreateItemVariationRequest> Variations { get; set; } = new();

    // ── Warranty and Shipping ──────────────────────────────────────────────
    public string? WarrantyType { get; set; }  // "Garantia do fabricante" | "Garantia do vendedor"
    public string? WarrantyTime { get; set; }  // "90 dias" | "6 meses" | "12 meses" | "24 meses"
    public bool FreeShipping { get; set; } = false;
    public decimal? WidthCm { get; set; }
    public decimal? HeightCm { get; set; }
    public decimal? LengthCm { get; set; }
    public decimal? WeightKg { get; set; }
}

public sealed class MercadoLivreCreateItemAttributeRequest
{
    public string Id { get; set; } = string.Empty;
    public string? ValueId { get; set; }
    public string? ValueName { get; set; }
}

public sealed class MercadoLivreCreateItemResult
{
    public string ItemId { get; set; } = string.Empty;
    public string? VariationId { get; set; }
    public string? Permalink { get; set; }
    public string? ApiUrl { get; set; }
    public List<MercadoLivreCreateItemVariationResult> Variations { get; set; } = new();
}

public sealed class MercadoLivreCreateItemVariationRequest
{
    public string SabrVariantSku { get; set; } = string.Empty;
    public decimal? Price { get; set; }
    public int AvailableQuantity { get; set; }
    public List<MercadoLivreCreateItemVariationAttributeRequest> AttributeCombinations { get; set; } = new();
    public List<string> PictureUrls { get; set; } = new();
}

public sealed class MercadoLivreCreateItemVariationAttributeRequest
{
    public string Id { get; set; } = string.Empty;
    public string? ValueId { get; set; }
    public string? ValueName { get; set; }
}

public sealed class MercadoLivreCreateItemVariationResult
{
    public string? SabrVariantSku { get; set; }
    public string? VariationId { get; set; }
}

public sealed class MercadoLivreCategoryCapabilityResponse
{
    public string? CategoryName { get; set; }
    public string? CategoryPathFromRoot { get; set; }
    public bool IsLeaf { get; set; } = true;
    public bool AllowsVariations { get; set; }
    public int MaxVariationsAllowed { get; set; }
    public int MaxVariationAttributes { get; set; }
    public List<string> AllowedVariationAttributes { get; set; } = new();
}

public sealed class MercadoLivreCategoryAttributeResponse
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Required { get; set; }
    public bool Conditional { get; set; }
    public bool IsVariation { get; set; }
    public string? ValueType { get; set; }
    public List<MercadoLivreCategoryAttributeValueResponse> Values { get; set; } = new();
    public Dictionary<string, string> Tags { get; set; } = new();
}

public sealed class MercadoLivreCategoryAttributeValueResponse
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class MercadoLivreDomainDiscoverySuggestion
{
    public string CategoryId { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string? DomainId { get; set; }
    public string? DomainName { get; set; }
    public decimal? Score { get; set; }
    public string? PathFromRoot { get; set; }
}
