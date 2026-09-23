using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

/// <summary>
/// Immutable financial fact. Corrections are represented by a new entry linked
/// through SupersedesEntryId; existing rows must never be edited or deleted.
/// </summary>
public sealed class MarketplaceFinancialEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public MarketplaceProvider Provider { get; set; } = MarketplaceProvider.MercadoLivre;
    public long SellerId { get; set; }
    public string EntryType { get; set; } = string.Empty;
    public string Layer { get; set; } = FinancialLayers.Operational;
    public string Status { get; set; } = FinancialEntryStatuses.Estimated;
    public long AmountCents { get; set; }
    public string CurrencyId { get; set; } = "BRL";
    public string EconomicKey { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public Guid? SupersedesEntryId { get; set; }
    public Guid? MarketplaceOrderId { get; set; }
    public Guid? MarketplaceOrderItemId { get; set; }
    public string? ExternalOrderId { get; set; }
    public string? ExternalPaymentId { get; set; }
    public string? ExternalShipmentId { get; set; }
    public string? ExternalPackId { get; set; }
    public string? ExternalClaimId { get; set; }
    public string? ExternalReturnId { get; set; }
    public DateTimeOffset EconomicOccurredAt { get; set; }
    public DateTimeOffset? FinancialConfirmedAt { get; set; }
    public DateTimeOffset? ProviderUpdatedAt { get; set; }
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public string SourceEndpoint { get; set; } = string.Empty;
    public string? SourceRecordId { get; set; }
    public string CanonicalPayloadHash { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class FinancialEntryTypes
{
    public const string GrossSale = "GROSS_SALE";
    public const string BuyerDiscount = "BUYER_DISCOUNT";
    public const string SaleFee = "SALE_FEE";
    public const string FinancingOrFixedFee = "FINANCING_OR_FIXED_FEE";
    public const string SellerShippingCost = "SELLER_SHIPPING_COST";
    public const string ShippingDiscountOrCompensation = "SHIPPING_DISCOUNT_OR_COMPENSATION";
    public const string Refund = "REFUND";
    public const string ChargebackOrClaim = "CHARGEBACK_OR_CLAIM";
    public const string ReturnShippingCost = "RETURN_SHIPPING_COST";
    public const string ProductCost = "PRODUCT_COST";
    public const string ProductCostRecovery = "PRODUCT_COST_RECOVERY";
    public const string PlatformAdjustment = "PLATFORM_ADJUSTMENT";
    public const string SellerTaxEstimate = "SELLER_TAX_ESTIMATE";
}

public static class FinancialLayers
{
    public const string Operational = "OPERATIONAL";
    public const string Reconciled = "RECONCILED";
    public const string InternalConfirmed = "INTERNAL_CONFIRMED";
}

public static class FinancialEntryStatuses
{
    public const string Estimated = "ESTIMATED";
    public const string Confirmed = "CONFIRMED";
    /// <summary>
    /// The fact remains immutable and auditable, but no longer has economic
    /// effect. The amount is preserved because the ledger forbids zero-value
    /// rows; aggregators must ignore a VOIDED active head.
    /// </summary>
    public const string Voided = "VOIDED";
}

public static class FinancialMaturity
{
    public const string Incomplete = "INCOMPLETO";
    public const string Estimated = "ESTIMADO";
    public const string PartiallyConfirmed = "PARCIALMENTE_CONFIRMADO";
    public const string Confirmed = "CONFIRMADO";
    public const string Reopened = "REABERTO";
}
