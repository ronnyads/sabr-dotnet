using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Models;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Infrastructure.Persistence;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings__Default is required.");

var apply = args.Any(x => string.Equals(x, "--apply", StringComparison.OrdinalIgnoreCase));
var rebuild = args.Any(x => string.Equals(x, "--rebuild", StringComparison.OrdinalIgnoreCase));
var baselineDryRun = args.Any(x => string.Equals(x, "--baseline-dry-run", StringComparison.OrdinalIgnoreCase));
var preparePriceVersions = args.Any(x => string.Equals(x, "--prepare-price-versions", StringComparison.OrdinalIgnoreCase));
var activatePlanArg = ReadArgument(args, "--activate-plan");
var planHashArg = ReadArgument(args, "--plan-hash");
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
