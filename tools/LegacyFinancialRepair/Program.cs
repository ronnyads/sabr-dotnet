using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Options;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Infrastructure.Integrations.MercadoLivre;
using Phub.Infrastructure.Persistence;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings__Default is required.");

var apply = args.Any(x => string.Equals(x, "--apply", StringComparison.OrdinalIgnoreCase));
var rebuild = args.Any(x => string.Equals(x, "--rebuild", StringComparison.OrdinalIgnoreCase));
var baselineDryRun = args.Any(x => string.Equals(x, "--baseline-dry-run", StringComparison.OrdinalIgnoreCase));
var preparePriceVersions = args.Any(x => string.Equals(x, "--prepare-price-versions", StringComparison.OrdinalIgnoreCase));
var rebuildRepairedParallel = args.Any(x => string.Equals(x, "--rebuild-repaired-parallel", StringComparison.OrdinalIgnoreCase));
var activatePlanArg = ReadArgument(args, "--activate-plan");
var reportPlanArg = ReadArgument(args, "--report-plan");
var planHashArg = ReadArgument(args, "--plan-hash");
var syncOrderArg = ReadArgument(args, "--sync-order");
var email = ReadArgument(args, "--email");
var sellerArg = ReadArgument(args, "--seller");
await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
    .UseNpgsql(connectionString, options => options.CommandTimeout(180)).Options);
var ledger = new FinancialLedgerService(db);
var historical = new HistoricalProductCostService(db);
var projection = new OperationalFinancialProjectionService(db, ledger, historical);
var service = new LegacyFinancialCostRepairService(db, projection, historical);

LegacyFinancialCostRepairScope? scope = null;
if (!string.IsNullOrWhiteSpace(email) || !string.IsNullOrWhiteSpace(sellerArg))
{
    if (string.IsNullOrWhiteSpace(email) || !long.TryParse(sellerArg, out var sellerId))
        throw new InvalidOperationException("--email and --seller must be provided together.");
    var client = await db.Clients.AsNoTracking()
        .SingleOrDefaultAsync(x => x.Email.ToLower() == email.Trim().ToLower())
        ?? throw new InvalidOperationException($"Client {email} was not found.");
    var ownsSeller = await db.MarketplaceOrders.AsNoTracking().AnyAsync(x =>
        x.TenantId == client.TenantId && x.ClientId == client.Id && x.SellerId == sellerId);
    if (!ownsSeller)
        throw new InvalidOperationException($"Seller {sellerId} does not belong to client {email}.");
    scope = new LegacyFinancialCostRepairScope(client.TenantId, client.Id, sellerId);
}

if (!string.IsNullOrWhiteSpace(syncOrderArg))
{
    if (scope == null)
        throw new InvalidOperationException("Directed order sync requires --email and --seller.");

    var mlOptions = new MercadoLivreOptions
    {
        ClientId = Environment.GetEnvironmentVariable("MercadoLivre__ClientId") ?? string.Empty,
        ClientSecret = Environment.GetEnvironmentVariable("MercadoLivre__ClientSecret") ?? string.Empty,
        RedirectUri = Environment.GetEnvironmentVariable("MercadoLivre__RedirectUri") ?? string.Empty
    };
    using var httpClient = new HttpClient
    {
        BaseAddress = new Uri(mlOptions.ApiBaseUrl),
        Timeout = TimeSpan.FromSeconds(30)
    };
    IMercadoLivreApiClient apiClient = new MercadoLivreApiClient(httpClient, Options.Create(mlOptions));
    var oauth = new MercadoLivreOAuthService(db, apiClient, Options.Create(mlOptions));
    var allocations = new StockReservationAllocationService(db);
    var inventory = new MarketplaceOrderInventoryService(db, allocations);
    var catalogAuthorization = new CatalogAuthorizationService(db);
    var mapping = new MarketplaceOrderMappingService(db, catalogAuthorization, inventory, projection,
        NullLogger<MarketplaceOrderMappingService>.Instance);
    var sync = new MercadoLivreSyncService(
        db,
        apiClient,
        oauth,
        new StockAvailabilityService(db, apiClient, oauth, NullLogger<StockAvailabilityService>.Instance,
            Options.Create(mlOptions)),
        new MarketplaceOrderNumberService(db),
        new MarketplaceAuditLogService(db),
        mapping,
        inventory,
        projection,
        allocations,
        Options.Create(mlOptions),
        NullLogger<MercadoLivreSyncService>.Instance);

    var syncResult = await sync.SyncOrderNowAsync(
        scope.TenantId, scope.ClientId, scope.SellerId, syncOrderArg.Trim());
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        mode = "DIRECTED_ORDER_SYNC",
        scope,
        orderId = syncOrderArg.Trim(),
        syncResult.Succeeded,
        errors = syncResult.Errors,
        syncResult.Data
    }));
    return;
}

if (baselineDryRun)
{
    if (scope == null)
        throw new InvalidOperationException("Baseline dry-run requires --email and --seller.");
    var actorId = await db.PlatformUsers.AsNoTracking()
        .Where(x => x.IsActive && x.Role == PlatformUserRole.SuperAdmin)
        .OrderBy(x => x.CreatedAt)
        .Select(x => x.Id)
        .FirstOrDefaultAsync();
    if (actorId == Guid.Empty)
        throw new InvalidOperationException("No active SuperAdmin actor was found.");

    var variants = await (from item in db.MarketplaceOrderItems.AsNoTracking()
                          join variant in db.ProductVariants.AsNoTracking()
                              on item.SabrVariantSku equals variant.VariantSku
                          where item.TenantId == scope.TenantId
                                && item.ClientId == scope.ClientId
                                && item.SellerId == scope.SellerId
                                && variant.IsActive
                                && variant.CatalogCostStatus == CatalogCostStatuses.Resolved
                                && variant.CatalogPriceCents > 0
                          select variant)
        .Distinct()
        .OrderBy(x => x.VariantSku)
        .ToListAsync();
    var cutAt = DateTimeOffset.UtcNow;
    var variantSkus = variants.Select(x => x.VariantSku).ToArray();
    var openVersionSkus = await db.ProductPriceVersions.AsNoTracking()
        .Where(x => x.VariantSku != null && variantSkus.Contains(x.VariantSku) && x.ValidTo == null)
        .GroupBy(x => x.VariantSku!)
        .Select(x => new { Sku = x.Key, Count = x.Count() })
        .ToListAsync();
    var openCounts = openVersionSkus.ToDictionary(x => x.Sku, x => x.Count, StringComparer.Ordinal);
    var missingVersions = variantSkus.Where(x => !openCounts.ContainsKey(x)).Order().ToArray();
    var overlappingVersions = openCounts.Where(x => x.Value > 1).OrderBy(x => x.Key).ToArray();
    if (overlappingVersions.Length != 0 || missingVersions.Length != 0 && !preparePriceVersions)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            mode = "BASELINE_PREFLIGHT_FAILED",
            scope,
            missingVersions,
            overlappingVersions
        }));
        return;
    }
    if (missingVersions.Length != 0)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var sku in missingVersions)
        {
            var variant = variants.Single(x => x.VariantSku == sku);
            var latestVersion = await db.ProductPriceVersions
                .Where(x => x.VariantSku == sku)
                .MaxAsync(x => (long?)x.Version) ?? 0;
            db.ProductPriceVersions.Add(new ProductPriceVersion
            {
                ProductSku = variant.BaseSku,
                VariantSku = variant.VariantSku,
                PricingMode = variant.PricingMode,
                CostPriceCents = variant.CostPriceCents,
                CatalogPriceCents = variant.CatalogPriceCents,
                CatalogCostStatus = variant.CatalogCostStatus,
                CatalogPriceOrigin = variant.CatalogPriceOrigin,
                ValidFrom = cutAt,
                Version = latestVersion + 1,
                ChangeType = ProductPriceChangeTypes.Correction,
                ChangedByUserId = actorId,
                Reason = "Criação auditada da versão inicial necessária ao baseline retroativo aprovado",
                CreatedAt = cutAt
            });
            db.AuditEvents.Add(new AuditEvent
            {
                TenantId = scope.TenantId,
                ActorType = "USER",
                ActorId = actorId,
                Action = "CatalogPriceVersion.CreateMissingForBaseline",
                Entity = $"ProductVariant:{sku}",
                RequestId = Guid.NewGuid(),
                MetadataJson = JsonSerializer.Serialize(new
                {
                    scope.ClientId,
                    scope.SellerId,
                    variant.BaseSku,
                    variant.VariantSku,
                    variant.CatalogPriceCents,
                    reason = "Preparação manual para dry-run do baseline"
                }),
                CreatedAt = cutAt
            });
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }
    var request = new FinancialCostCorrectionDryRunRequest
    {
        PlanType = FinancialCorrectionPlanTypes.CatalogBaselineCurrent,
        SellerId = scope.SellerId,
        Reason = "Baseline manual aprovado após reconciliação com relatório oficial de vendas do Mercado Livre",
        Skus = variants.Select(x => new FinancialCostCorrectionSkuRequest
        {
            Sku = x.VariantSku,
            CorrectUnitCostCents = x.CatalogPriceCents,
            BaselineCutAt = cutAt
        }).ToList()
    };
    var planService = new FinancialCostCorrectionPlanService(db, projection);
    var plan = await planService.DryRunAsync(scope.TenantId, scope.ClientId, request, actorId);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        mode = "BASELINE_DRY_RUN",
        scope,
        cutAt,
        skuCount = variants.Count,
        plan.PlanId,
        plan.PlanHash,
        plan.Status,
        plan.Report.TotalEntries,
        plan.Report.TotalProfitImpactCents,
        pending = plan.Report.Pending.Count,
        plan.Report.CostCoverageBefore,
        plan.Report.CostCoverageAfter,
        plan.Report.FinancialCoverage,
        history = plan.Report.HistoryCoverage
    }));
    return;
}

if (!string.IsNullOrWhiteSpace(activatePlanArg))
{
    if (scope == null)
        throw new InvalidOperationException("Plan activation requires --email and --seller.");
    if (!Guid.TryParse(activatePlanArg, out var planId) || string.IsNullOrWhiteSpace(planHashArg))
        throw new InvalidOperationException("--activate-plan must be a GUID and --plan-hash is required.");

    var actorId = await db.PlatformUsers.AsNoTracking()
        .Where(x => x.IsActive && x.Role == PlatformUserRole.SuperAdmin)
        .OrderBy(x => x.CreatedAt)
        .Select(x => x.Id)
        .FirstOrDefaultAsync();
    if (actorId == Guid.Empty)
        throw new InvalidOperationException("No active SuperAdmin actor was found.");

    var planService = new FinancialCostCorrectionPlanService(db, projection);
    var current = await planService.GetAsync(scope.TenantId, scope.ClientId, planId)
        ?? throw new InvalidOperationException($"Plan {planId} was not found in the requested scope.");
    if (!string.Equals(current.PlanHash, planHashArg.Trim(), StringComparison.Ordinal))
        throw new InvalidOperationException("The provided planHash does not match the persisted plan.");

    var command = new FinancialCorrectionPlanCommand
    {
        PlanHash = planHashArg.Trim(),
        Reason = "Ativação operacional explicitamente autorizada após validação do relatório de dry-run"
    };
    if (current.Status is FinancialCorrectionPlanStatuses.DryRun or FinancialCorrectionPlanStatuses.AwaitingApproval)
        current = await planService.ApproveAsync(scope.TenantId, scope.ClientId, planId, command, actorId);

    while (current.Status is FinancialCorrectionPlanStatuses.Preparing or FinancialCorrectionPlanStatuses.Failed)
        current = await planService.ResumeAsync(scope.TenantId, scope.ClientId, planId, command, actorId);

    if (current.Status == FinancialCorrectionPlanStatuses.PendingActivation)
        current = await planService.ActivateAsync(scope.TenantId, scope.ClientId, planId, command, actorId);
    else if (current.Status == FinancialCorrectionPlanStatuses.Reconciling)
        current = await planService.ResumeAsync(scope.TenantId, scope.ClientId, planId, command, actorId);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        mode = "ACTIVATE_PLAN",
        scope,
        current.PlanId,
        current.PlanHash,
        current.Status,
        current.Report.TotalEntries,
        current.Report.TotalProfitImpactCents,
        pending = current.Report.Pending.Count,
        current.Report.CostCoverageBefore,
        current.Report.CostCoverageAfter,
        current.Report.FinancialCoverage
    }));
    return;
}

if (!string.IsNullOrWhiteSpace(reportPlanArg))
{
    if (scope == null)
        throw new InvalidOperationException("Plan report requires --email and --seller.");
    if (!Guid.TryParse(reportPlanArg, out var reportPlanId))
        throw new InvalidOperationException("--report-plan must be a GUID.");
    var planService = new FinancialCostCorrectionPlanService(db, projection);
    var current = await planService.GetAsync(scope.TenantId, scope.ClientId, reportPlanId)
        ?? throw new InvalidOperationException($"Plan {reportPlanId} was not found in the requested scope.");
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        mode = "PLAN_REPORT",
        scope,
        current.PlanId,
        current.PlanHash,
        current.Status,
        current.Report.TotalEntries,
        current.Report.TotalProfitImpactCents,
        current.Report.CostCoverageBefore,
        current.Report.CostCoverageAfter,
        current.Report.FinancialCoverage,
        pendingByCode = current.Report.Pending.GroupBy(x => x.Code)
            .Select(x => new { code = x.Key, count = x.Count(), unitsCostCents = x.Sum(y => y.ObservedCostCents ?? 0) })
            .OrderByDescending(x => x.count),
        pendingBySku = current.Report.Pending.GroupBy(x => x.Sku ?? "(sem SKU)")
            .Select(x => new { sku = x.Key, count = x.Count(), codes = x.Select(y => y.Code).Distinct().Order() })
            .OrderByDescending(x => x.count).Take(30),
        skus = current.Report.Skus.OrderByDescending(x => Math.Abs(x.ProfitImpactCents))
    }));
    return;
}

if (rebuildRepairedParallel)
{
    if (scope == null)
        throw new InvalidOperationException("Parallel repaired-order rebuild requires --email and --seller.");

    const string repairEndpoint = "financial-repair/legacy-product-cost-v1";
    var repairWatermark = await db.MarketplaceFinancialEntries.AsNoTracking()
        .Where(x => x.TenantId == scope.TenantId && x.ClientId == scope.ClientId
            && x.SellerId == scope.SellerId && x.SourceEndpoint == repairEndpoint)
        .MaxAsync(x => (DateTimeOffset?)x.CreatedAt)
        ?? throw new InvalidOperationException("No legacy repair entries were found in the requested scope.");
    var repairedOrderIds = await (from head in db.FinancialEconomicHeads.AsNoTracking()
                                  join entry in db.MarketplaceFinancialEntries.AsNoTracking()
                                      on head.ActiveEntryId equals entry.Id
                                  where entry.TenantId == scope.TenantId && entry.ClientId == scope.ClientId
                                        && entry.SellerId == scope.SellerId
                                        && entry.SourceEndpoint == repairEndpoint
                                        && entry.MarketplaceOrderId.HasValue
                                  select entry.MarketplaceOrderId!.Value)
        .Distinct().ToListAsync();
    var projectedIds = await db.MarketplaceOrderFinancialStates.AsNoTracking()
        .Where(x => repairedOrderIds.Contains(x.MarketplaceOrderId) && x.LastProjectedAt >= repairWatermark)
        .Select(x => x.MarketplaceOrderId).ToListAsync();
    var projected = projectedIds.ToHashSet();
    var remaining = repairedOrderIds.Where(x => !projected.Contains(x)).Order().ToArray();
    var completed = 0;
    await Parallel.ForEachAsync(remaining, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (orderId, ct) =>
    {
        await using var workerDb = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, options => options.CommandTimeout(180)).Options);
        var workerLedger = new FinancialLedgerService(workerDb);
        var workerHistorical = new HistoricalProductCostService(workerDb);
        var workerProjection = new OperationalFinancialProjectionService(workerDb, workerLedger, workerHistorical);
        var order = await workerDb.MarketplaceOrders.Include(x => x.Items)
            .SingleAsync(x => x.Id == orderId && x.TenantId == scope.TenantId
                && x.ClientId == scope.ClientId && x.SellerId == scope.SellerId, ct);
        await workerProjection.RebuildOrderStateAsync(order, ct);
        Interlocked.Increment(ref completed);
    });
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        mode = "PARALLEL_REBUILD_REPAIRED",
        scope,
        repairWatermark,
        total = repairedOrderIds.Count,
        alreadyProjected = projected.Count,
        rebuilt = completed
    }));
    return;
}

var result = await service.RepairAllAsync(apply, rebuild, scope);
Console.WriteLine(JsonSerializer.Serialize(new
{
    mode = apply ? "APPLY" : "DRY_RUN",
    rebuildOrderStates = rebuild,
    scope,
    result
}));

static string? ReadArgument(string[] values, string name)
{
    var index = Array.FindIndex(values, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}
