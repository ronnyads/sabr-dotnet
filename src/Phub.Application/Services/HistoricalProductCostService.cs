using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Domain.Entities;

namespace Phub.Application.Services;

public sealed record HistoricalProductCost(Guid VersionId, long Version, long CatalogPriceCents,
    long CostPriceCents, string Origin, DateTimeOffset EconomicAt, string EconomicAtSource,
    Guid? BaselineId = null);

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

        var baseSku = await _db.ProductVariants.AsNoTracking()
            .Where(x => x.VariantSku == item.SabrVariantSku)
            .Select(x => x.BaseSku)
            .SingleOrDefaultAsync(cancellationToken);

        // An approved baseline is a persistent domain rule. It must be resolved
        // before legacy publication snapshots; otherwise an order imported after
        // approval could silently revive the historical, incorrect cost.
        var baselines = await _db.CatalogCostBaselines.AsNoTracking()
            .Where(x => x.VariantSku == item.SabrVariantSku
                        && x.Status == CatalogCostBaselineStatuses.Active)
            .OrderByDescending(x => x.BaselineCutAt)
            .Take(2)
            .ToListAsync(cancellationToken);
        if (baselines.Count > 1) return null;
        var baseline = baselines.SingleOrDefault();
        if (baseline != null)
        {
            CatalogCostBaselinePolicy.EnsureValid(baseline);
            if (economicAt < baseline.BaselineCutAt)
                return new HistoricalProductCost(baseline.BaselinePriceVersionId, 0, baseline.BaselineUnitCostCents,
                    baseline.BaselineUnitCostCents, baseline.CostOrigin, economicAt, source, baseline.Id);
        }

        // Without an approved baseline, a publication remains the client-scoped
        // commercial snapshot. It is an internal catalog snapshot, never the
        // listing or order sale price.
        var publicationSkus = string.IsNullOrWhiteSpace(baseSku)
            ? new[] { item.SabrVariantSku }
            : new[] { item.SabrVariantSku, baseSku };
        if (baseline == null)
        {
            var publication = await _db.Publications.AsNoTracking()
                .Where(x => x.TenantId == order.TenantId && x.ClientId == order.ClientId
                            && publicationSkus.Contains(x.ProductSku)
                            && x.CatalogPriceCentsSnapshot > 0)
                .OrderByDescending(x => x.ProductSku == item.SabrVariantSku)
                .ThenByDescending(x => x.PriceSnapshotTakenAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (publication != null)
                return new HistoricalProductCost(Guid.Empty, 0, publication.CatalogPriceCentsSnapshot,
                    publication.CostPriceCentsSnapshot, CatalogPriceOrigins.PublicationSnapshot,
                    economicAt, source);
        }
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
