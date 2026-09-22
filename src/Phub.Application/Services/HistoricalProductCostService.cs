using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Domain.Entities;

namespace Phub.Application.Services;

public sealed record HistoricalProductCost(Guid VersionId, long Version, long CatalogPriceCents,
    long CostPriceCents, string Origin, DateTimeOffset EconomicAt, string EconomicAtSource);

public sealed class HistoricalProductCostService
{
    private readonly IAppDbContext _db;
    public HistoricalProductCostService(IAppDbContext db) => _db = db;

    public async Task<HistoricalProductCost?> ResolveAsync(MarketplaceOrder order, MarketplaceOrderItem item,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(item.SabrVariantSku)) return null;
        var economicAt = order.PaidAt ?? order.ChannelCreatedAt ?? order.CreatedAt;
        var source = order.PaidAt.HasValue ? "PAID_AT" : order.ChannelCreatedAt.HasValue ? "CHANNEL_CREATED_AT" : "IMPORTED_AT";
        var version = await _db.ProductPriceVersions.AsNoTracking()
            .Where(x => x.VariantSku == item.SabrVariantSku && x.ValidFrom <= economicAt
                        && (x.ValidTo == null || economicAt < x.ValidTo))
            .OrderByDescending(x => x.ValidFrom).FirstOrDefaultAsync(cancellationToken);
        if (version == null && !_db.Database.IsRelational())
        {
            var legacy = await _db.ProductVariants.AsNoTracking().SingleOrDefaultAsync(x => x.VariantSku == item.SabrVariantSku, cancellationToken);
            if (legacy == null) return null;
            return new HistoricalProductCost(Guid.Empty, 0, legacy.CatalogPriceCents, legacy.CostPriceCents,
                legacy.PricingMode == ProductPricingModes.Inherited ? "MASTER_PRODUCT" : "VARIANT_OVERRIDE",
                economicAt, source);
        }
        if (version == null) return null;
        return new HistoricalProductCost(version.Id, version.Version, version.CatalogPriceCents,
            version.CostPriceCents,
            version.PricingMode == ProductPricingModes.Inherited ? "MASTER_PRODUCT" : "VARIANT_OVERRIDE",
            economicAt, source);
    }
}
