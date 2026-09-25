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
        var resolvedEconomicAt = ResolveEconomicAt(order);
        if (resolvedEconomicAt == null) return null;
        var (economicAt, source) = resolvedEconomicAt.Value;
        var versions = await _db.ProductPriceVersions.AsNoTracking()
            .Where(x => x.VariantSku == item.SabrVariantSku && x.ValidFrom <= economicAt
                        && (x.ValidTo == null || economicAt < x.ValidTo))
            .OrderByDescending(x => x.ValidFrom)
            .Take(2)
            .ToListAsync(cancellationToken);

        // Missing or overlapping versions are both unresolved. Picking one in either case
        // would silently rewrite the economic history of the order.
        if (versions.Count > 1) return null;
        var version = versions.SingleOrDefault();
        if (version == null && !_db.Database.IsRelational())
        {
            var legacy = await _db.ProductVariants.AsNoTracking().SingleOrDefaultAsync(x => x.VariantSku == item.SabrVariantSku, cancellationToken);
            if (legacy == null || legacy.CatalogCostStatus != CatalogCostStatuses.Resolved
                               || legacy.CatalogPriceCents <= 0) return null;
            return new HistoricalProductCost(Guid.Empty, 0, legacy.CatalogPriceCents, legacy.CostPriceCents,
                legacy.CatalogPriceOrigin,
                economicAt, source);
        }
        if (version == null || version.CatalogCostStatus != CatalogCostStatuses.Resolved
                            || version.CatalogPriceCents <= 0
                            || !CatalogPriceOrigins.IsAuthorizedCatalogOrigin(version.CatalogPriceOrigin)) return null;
        return new HistoricalProductCost(version.Id, version.Version, version.CatalogPriceCents,
            version.CostPriceCents,
            version.CatalogPriceOrigin,
            economicAt, source);
    }

    internal static (DateTimeOffset EconomicAt, string Source)? ResolveEconomicAt(MarketplaceOrder order)
    {
        if (order.PaidAt.HasValue) return (order.PaidAt.Value, "PAID_AT");
        if (order.ChannelCreatedAt.HasValue) return (order.ChannelCreatedAt.Value, "CHANNEL_CREATED_AT");
        return null;
    }
}
