using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Infrastructure.Persistence;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings__Default is required.");

var sku = ReadArgument(args, "--sku")?.Trim().ToUpperInvariant();
if (string.IsNullOrWhiteSpace(sku))
    throw new InvalidOperationException("--sku is required.");

var apply = args.Any(x => string.Equals(x, "--apply", StringComparison.OrdinalIgnoreCase));
var reason = ReadArgument(args, "--reason")?.Trim()
             ?? "Correção retroativa de custo informado incorretamente pelo fornecedor";

await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
    .UseNpgsql(connectionString, options => options.CommandTimeout(300)).Options);

var product = await db.Products.SingleOrDefaultAsync(x => x.Sku == sku)
              ?? throw new InvalidOperationException($"Product {sku} was not found.");
if (product.CatalogPriceCents <= 0 || product.CostPriceCents <= 0)
    throw new InvalidOperationException($"Product {sku} does not have a valid corrected cost.");
if (product.CatalogPriceCents != product.CostPriceCents)
    throw new InvalidOperationException($"Product {sku} cost and catalog cost must match before correction.");

var targetCost = product.CatalogPriceCents;
var itemsQuery = db.MarketplaceOrderItems.Where(x => x.SabrVariantSku == sku);
var itemCount = await itemsQuery.CountAsync();
var unitCount = await itemsQuery.SumAsync(x => (long?)x.Quantity) ?? 0;
var orderCount = await itemsQuery.Select(x => x.MarketplaceOrderId).Distinct().CountAsync();
var accountCount = await itemsQuery.Select(x => new { x.TenantId, x.ClientId, x.SellerId }).Distinct().CountAsync();
var settledCount = await (from item in itemsQuery
                          join order in db.MarketplaceOrders on item.MarketplaceOrderId equals order.Id
                          where order.SabrPaymentConfirmedAt != null || item.CostSettledAt != null
                          select item.Id).CountAsync();

var activeCosts = await (from head in db.FinancialEconomicHeads.AsNoTracking()
                         join entry in db.MarketplaceFinancialEntries.AsNoTracking()
                             on head.ActiveEntryId equals entry.Id
                         join item in itemsQuery on entry.MarketplaceOrderItemId equals item.Id
                         where entry.EntryType == FinancialEntryTypes.ProductCost
                         group new { entry, item } by new
                         {
                             entry.Status,
                             UnitCost = item.Quantity == 0 ? 0 : Math.Abs(entry.AmountCents) / item.Quantity
                         }
                         into grouped
                         orderby grouped.Key.Status, grouped.Key.UnitCost
                         select new
                         {
                             grouped.Key.Status,
                             grouped.Key.UnitCost,
                             Rows = grouped.Count(),
                             Units = grouped.Sum(x => x.item.Quantity)
                         }).ToListAsync();

if (!apply)
{
    WriteResult("DRY_RUN", sku, targetCost, itemCount, unitCount, orderCount, accountCount,
        settledCount, activeCosts, 0, 0, 0);
    return;
}

if (settledCount > 0)
    throw new InvalidOperationException(
        $"Correction blocked: {settledCount} item(s) already have settled wallet costs and require a recovery plan.");

var changedAt = DateTimeOffset.UtcNow;
var actorId = await db.ProductPriceHistories.AsNoTracking()
    .Where(x => x.ProductSku == sku)
    .OrderByDescending(x => x.ChangedAt)
    .Select(x => x.ChangedByUserId)
    .FirstOrDefaultAsync();
if (actorId == Guid.Empty)
    throw new InvalidOperationException($"Product {sku} has no auditable actor for the correction.");

await using (var transaction = await db.Database.BeginTransactionAsync())
{
    var variant = await db.ProductVariants.SingleOrDefaultAsync(x => x.VariantSku == sku);
    Guid? correctionVersionId = null;
    if (variant != null)
    {
        var versions = await db.ProductPriceVersions
            .Where(x => x.ProductSku == variant.BaseSku && x.VariantSku == variant.VariantSku)
            .OrderBy(x => x.Version)
            .ToListAsync();
        var openVersions = versions.Where(x => x.ValidTo == null).ToArray();
        if (openVersions.Length > 1)
            throw new InvalidOperationException($"Variant {sku} has overlapping active price versions.");
        var open = openVersions.SingleOrDefault();
        var alreadyCorrected = variant.CostPriceCents == targetCost
                               && variant.CatalogPriceCents == targetCost
                               && variant.PricingMode == ProductPricingModes.Inherited
                               && variant.CatalogPriceOrigin == CatalogPriceOrigins.MasterProduct
                               && open?.CostPriceCents == targetCost
                               && open.CatalogPriceCents == targetCost
                               && open.ChangeType == ProductPriceChangeTypes.Correction;
        if (alreadyCorrected)
        {
            correctionVersionId = open!.Id;
        }
        else
        {
            if (open != null)
                open.ValidTo = changedAt > open.ValidFrom ? changedAt : open.ValidFrom.AddTicks(1);

            variant.CostPriceCents = targetCost;
            variant.CatalogPriceCents = targetCost;
            variant.PricingMode = ProductPricingModes.Inherited;
            variant.CatalogCostStatus = CatalogCostStatuses.Resolved;
            variant.CatalogPriceOrigin = CatalogPriceOrigins.MasterProduct;
            variant.UpdatedAt = open?.ValidTo ?? changedAt;

            var correctionVersion = new ProductPriceVersion
            {
                ProductSku = variant.BaseSku,
                VariantSku = variant.VariantSku,
                PricingMode = ProductPricingModes.Inherited,
                CostPriceCents = targetCost,
                CatalogPriceCents = targetCost,
                CatalogCostStatus = CatalogCostStatuses.Resolved,
                CatalogPriceOrigin = CatalogPriceOrigins.MasterProduct,
                ValidFrom = variant.UpdatedAt,
                Version = (versions.LastOrDefault()?.Version ?? 0) + 1,
                ChangeType = ProductPriceChangeTypes.Correction,
                ChangedByUserId = actorId,
                Reason = reason,
                CreatedAt = changedAt
            };
            correctionVersionId = correctionVersion.Id;
            db.ProductPriceVersions.Add(correctionVersion);
        }
    }

    var publications = await db.Publications.Where(x => x.ProductSku == sku).ToListAsync();
    foreach (var publication in publications)
    {
        publication.CostPriceCentsSnapshot = targetCost;
        publication.CatalogPriceCentsSnapshot = targetCost;
        publication.PriceSnapshotTakenAt = changedAt;
        publication.UpdatedAt = changedAt;
    }

    var items = await itemsQuery.ToListAsync();
    foreach (var item in items)
    {
        item.CatalogUnitPriceCentsAtPayment = targetCost;
        item.CostUnitPriceCentsAtPayment = targetCost;
        item.ChargeLineTotalCentsAtPayment = checked(targetCost * item.Quantity);
        item.CostSource = "CATALOG_PRICE_CORRECTION";
        item.CatalogPriceVersionId = correctionVersionId;
        item.CatalogCostBaselineId = null;
        item.CostReferencesJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                source = CatalogPriceOrigins.MasterProduct,
                quantity = item.Quantity,
                unitCostCents = targetCost,
                catalogPriceVersionId = correctionVersionId,
                correctionReason = reason
            }
        });
        item.UpdatedAt = changedAt;
    }

    foreach (var account in items.Select(x => new { x.TenantId, x.ClientId, x.SellerId }).Distinct())
    {
        db.AuditEvents.Add(new AuditEvent
        {
            TenantId = account.TenantId,
            ActorType = "USER",
            ActorId = actorId,
            Action = "SupplierCost.CorrectHistoricalOrders",
            Entity = $"Product:{sku}",
            RequestId = Guid.NewGuid(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                sku,
                targetCostCents = targetCost,
                account.ClientId,
                account.SellerId,
                reason
            }),
            CreatedAt = changedAt
        });
    }

    await db.SaveChangesAsync();
    await transaction.CommitAsync();
}

db.ChangeTracker.Clear();
var orderIds = await db.MarketplaceOrderItems.AsNoTracking()
    .Where(x => x.SabrVariantSku == sku)
    .Select(x => x.MarketplaceOrderId)
    .Distinct()
    .OrderBy(x => x)
    .ToListAsync();
var ledger = new FinancialLedgerService(db);
var historical = new HistoricalProductCostService(db);
var projection = new OperationalFinancialProjectionService(db, ledger, historical);
var projected = 0;
foreach (var orderId in orderIds)
{
    await projection.ProjectOrderAsync(orderId);
    projected++;
    db.ChangeTracker.Clear();
}

var correctedActiveCosts = await (from head in db.FinancialEconomicHeads.AsNoTracking()
                                  join entry in db.MarketplaceFinancialEntries.AsNoTracking()
                                      on head.ActiveEntryId equals entry.Id
                                  join item in db.MarketplaceOrderItems.AsNoTracking()
                                      on entry.MarketplaceOrderItemId equals item.Id
                                  where item.SabrVariantSku == sku
                                        && entry.EntryType == FinancialEntryTypes.ProductCost
                                  group new { entry, item } by new
                                  {
                                      entry.Status,
                                      UnitCost = item.Quantity == 0 ? 0 : Math.Abs(entry.AmountCents) / item.Quantity
                                  }
                                  into grouped
                                  orderby grouped.Key.Status, grouped.Key.UnitCost
                                  select new
                                  {
                                      grouped.Key.Status,
                                      grouped.Key.UnitCost,
                                      Rows = grouped.Count(),
                                      Units = grouped.Sum(x => x.item.Quantity)
                                  }).ToListAsync();
var currentItemCount = await db.MarketplaceOrderItems.CountAsync(x => x.SabrVariantSku == sku);
var currentUnitCount = await db.MarketplaceOrderItems.Where(x => x.SabrVariantSku == sku)
    .SumAsync(x => (long?)x.Quantity) ?? 0;
var mismatchedSnapshots = await db.MarketplaceOrderItems.CountAsync(x => x.SabrVariantSku == sku
    && (x.CatalogUnitPriceCentsAtPayment != targetCost || x.CostUnitPriceCentsAtPayment != targetCost));

WriteResult("APPLY", sku, targetCost, currentItemCount, currentUnitCount, orderIds.Count, accountCount,
    settledCount, correctedActiveCosts, projected, mismatchedSnapshots,
    correctedActiveCosts.Where(x => x.Status != FinancialEntryStatuses.Voided && x.UnitCost != targetCost).Sum(x => x.Rows));

static string? ReadArgument(string[] values, string name)
{
    var index = Array.FindIndex(values, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}

static void WriteResult(string mode, string sku, long targetCost, int items, long units, int orders,
    int accounts, int settled, object distribution, int projected, int mismatchedSnapshots, int mismatchedActiveCosts)
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        mode,
        sku,
        targetCostCents = targetCost,
        accounts,
        orders,
        items,
        units,
        settledItems = settled,
        projectedOrders = projected,
        mismatchedSnapshots,
        mismatchedActiveCosts,
        activeCostDistribution = distribution
    }));
}
