namespace Phub.Domain.Entities;

public sealed class SellerOwnedStockLot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public long SellerId { get; set; }
    public string VariantSku { get; set; } = string.Empty;
    public int OriginalQuantity { get; set; }
    public int AvailableQuantity { get; set; }
    public int ReservedQuantity { get; set; }
    public int ConsumedQuantity { get; set; }
    public long UnitCostCents { get; set; }
    public string CurrencyId { get; set; } = "BRL";
    public string SourceType { get; set; } = SellerOwnedStockLotSources.SettledPurchase;
    public string SourceId { get; set; } = string.Empty;
    public string? EvidenceReference { get; set; }
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid CreatedByUserId { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public static class SellerOwnedStockLotSources
{
    public const string SettledPurchase = "SETTLED_PURCHASE";
    public const string SuperAdminCorrection = "SUPERADMIN_CORRECTION";
}
