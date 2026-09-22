namespace Phub.Domain.Entities;

public sealed class StockReservationAllocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StockReservationId { get; set; }
    public Guid? SellerOwnedStockLotId { get; set; }
    public string Source { get; set; } = StockReservationSources.GeneralStock;
    public int Quantity { get; set; }
    public long? UnitCostCents { get; set; }
    public string CurrencyId { get; set; } = "BRL";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ConsumedAt { get; set; }
}

public static class StockReservationSources
{
    public const string GeneralStock = "GENERAL_STOCK";
    public const string PrePurchasedLot = "PREPURCHASED_LOT";
    public const string Mixed = "MIXED";
}
