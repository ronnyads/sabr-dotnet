using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Entities;
using Phub.Domain.ValueObjects;

namespace Phub.Application.Services;

/// <summary>
/// Builds an immutable correction manifest and stages corrections outside the ledger.
/// Only an explicit, compare-and-set activation can publish prepared replacements.
/// </summary>
public sealed class FinancialCostCorrectionPlanService
{
    private const int PreparationChunkSize = 100;
    private static readonly HashSet<string> FinancialCoverageTypes = new(StringComparer.Ordinal)
    {
        FinancialEntryTypes.GrossSale,
        FinancialEntryTypes.SaleFee,
        FinancialEntryTypes.FinancingOrFixedFee,
        FinancialEntryTypes.SellerShippingCost,
        FinancialEntryTypes.Refund,
        FinancialEntryTypes.PlatformAdjustment,
        FinancialEntryTypes.BuyerDiscount,
        FinancialEntryTypes.ShippingDiscountOrCompensation,
        FinancialEntryTypes.ChargebackOrClaim,
        FinancialEntryTypes.ReturnShippingCost
    };
    internal static readonly DateTimeOffset DefaultRangeFrom = new(2026, 9, 1, 3, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset DefaultRangeToExclusive = new(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);

    private readonly IAppDbContext _db;
    private readonly OperationalFinancialProjectionService? _projection;
    public FinancialCostCorrectionPlanService(IAppDbContext db, OperationalFinancialProjectionService? projection = null)
    {
        _db = db;
        _projection = projection;
    }

    public async Task<FinancialCostCorrectionDryRunResult> DryRunAsync(string tenantId, Guid clientId,
        FinancialCostCorrectionDryRunRequest request, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        ValidateRequest(tenantId, clientId, request, actorUserId);
        var isBaselinePlan = request.PlanType == FinancialCorrectionPlanTypes.CatalogBaselineCurrent;
        var historyCoverage = isBaselinePlan
            ? await RequireCompleteHistoryAsync(tenantId, clientId, request.SellerId, cancellationToken)
            : null;
        var rangeFrom = (request.RangeFrom
                         ?? historyCoverage?.RangeFrom
                         ?? DefaultRangeFrom).ToUniversalTime();
        var rangeToExclusive = (request.RangeToExclusive
                                ?? historyCoverage?.RangeTo
                                ?? DefaultRangeToExclusive).ToUniversalTime();
        if (rangeFrom >= rangeToExclusive) throw new ArgumentException("O início deve ser anterior ao fim exclusivo.");

        var corrections = isBaselinePlan
            ? await BuildBaselineRulesAsync(request, cancellationToken)
            : request.Skus.ToDictionary(
                x => Sku.Normalize(x.Sku),
                x => new CorrectionRule(x.CorrectUnitCostCents,
                    x.IncorrectCatalogPriceVersionIds.Distinct().Order().ToArray()),
                StringComparer.Ordinal);
        var selectedSkus = corrections.Keys.Order(StringComparer.Ordinal).ToArray();

        var rows = await (from head in _db.FinancialEconomicHeads.AsNoTracking()
                          join entry in _db.MarketplaceFinancialEntries.AsNoTracking() on head.ActiveEntryId equals entry.Id
                          join item in _db.MarketplaceOrderItems.AsNoTracking() on entry.MarketplaceOrderItemId equals item.Id
                          join order in _db.MarketplaceOrders.AsNoTracking() on item.MarketplaceOrderId equals order.Id
                          where head.TenantId == tenantId && head.ClientId == clientId && head.SellerId == request.SellerId
                                && entry.TenantId == tenantId && entry.ClientId == clientId && entry.SellerId == request.SellerId
                                && item.TenantId == tenantId && item.ClientId == clientId && item.SellerId == request.SellerId
                                && order.TenantId == tenantId && order.ClientId == clientId && order.SellerId == request.SellerId
                                && entry.EntryType == FinancialEntryTypes.ProductCost
                                && entry.EconomicOccurredAt >= rangeFrom && entry.EconomicOccurredAt < rangeToExclusive
                                && item.SabrVariantSku != null && selectedSkus.Contains(item.SabrVariantSku)
                          select new CandidateRow(head, entry, item, order)).ToListAsync(cancellationToken);

        var priceVersions = await _db.ProductPriceVersions.AsNoTracking()
            .Where(x => x.VariantSku != null && selectedSkus.Contains(x.VariantSku))
            .OrderBy(x => x.VariantSku).ThenBy(x => x.ValidFrom).ToListAsync(cancellationToken);

        var scopeOrders = await _db.MarketplaceOrders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientId == clientId && x.SellerId == request.SellerId)
            .Where(x => (x.PaidAt ?? x.ChannelCreatedAt) >= rangeFrom
                        && (x.PaidAt ?? x.ChannelCreatedAt) < rangeToExclusive)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var scopeItems = await _db.MarketplaceOrderItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientId == clientId && x.SellerId == request.SellerId
                        && scopeOrders.Contains(x.MarketplaceOrderId))
            .ToListAsync(cancellationToken);
        var scopeActiveEntries = await (from head in _db.FinancialEconomicHeads.AsNoTracking()
                                        join entry in _db.MarketplaceFinancialEntries.AsNoTracking()
                                            on head.ActiveEntryId equals entry.Id
                                        where head.TenantId == tenantId && head.ClientId == clientId
                                              && head.SellerId == request.SellerId
                                              && entry.Status != FinancialEntryStatuses.Voided
                                              && entry.MarketplaceOrderId.HasValue
                                              && scopeOrders.Contains(entry.MarketplaceOrderId.Value)
                                        select entry).ToListAsync(cancellationToken);

        var manifest = new List<FinancialCostCorrectionManifestEntry>();
        var pending = new List<FinancialCostCorrectionPendingItem>();

        foreach (var row in rows.OrderBy(x => x.Head.EconomicKey, StringComparer.Ordinal))
        {
            var sku = row.Item.SabrVariantSku!;
            var result = Evaluate(row, corrections[sku], priceVersions, isBaselinePlan);
            if (result.Pending != null)
            {
                pending.Add(result.Pending);
                continue;
            }

            if (result.Manifest != null)
            {
                manifest.Add(result.Manifest);
            }
        }

        var skuReports = manifest.GroupBy(x => x.Sku, StringComparer.Ordinal).Select(group =>
        {
            var current = group.Sum(x => x.CurrentCostCents);
            var replacement = group.Sum(x => x.ReplacementCostCents);
            return new FinancialCostCorrectionSkuReport(
                group.Key,
                group.Sum(x => x.CatalogQuantity + x.PrePurchasedQuantity),
                current,
                replacement,
                replacement - current,
                current - replacement,
                group.Count(),
                group.Sum(x => x.CatalogQuantity),
                group.Sum(x => x.PrePurchasedQuantity));
        }).OrderBy(x => x.Sku, StringComparer.Ordinal).ToArray();

        var totalUnits = scopeItems.Sum(x => Math.Max(0, x.Quantity));
        var productCostItemIds = scopeActiveEntries
            .Where(x => x.EntryType == FinancialEntryTypes.ProductCost && x.MarketplaceOrderItemId.HasValue)
            .Select(x => x.MarketplaceOrderItemId!.Value)
            .ToHashSet();
        var correctedItemIds = manifest.Select(x => x.MarketplaceOrderItemId).ToHashSet();
        var pendingItemIds = pending.Where(x => x.MarketplaceOrderItemId.HasValue)
            .Select(x => x.MarketplaceOrderItemId!.Value).ToHashSet();
        var beforeResolvedUnits = scopeItems.Where(x => IsCostResolved(x, productCostItemIds)
                                                        && !correctedItemIds.Contains(x.Id)
                                                        && !pendingItemIds.Contains(x.Id))
            .Sum(x => Math.Max(0, x.Quantity));
        var afterResolvedUnits = scopeItems.Where(x => IsCostResolved(x, productCostItemIds)
                                                       && !pendingItemIds.Contains(x.Id))
            .Sum(x => Math.Max(0, x.Quantity));
        var financialEntries = scopeActiveEntries.Where(x => FinancialCoverageTypes.Contains(x.EntryType)).ToArray();
        var financialTotal = financialEntries.Sum(x => Math.Abs(x.AmountCents));
        var financialResolved = financialEntries.Where(x => x.MarketplaceOrderItemId.HasValue)
            .Sum(x => Math.Abs(x.AmountCents));
        var baselineReports = isBaselinePlan
            ? corrections.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new CatalogCostBaselineReport(
                x.Value.ProductSku!, x.Key, x.Value.BaselinePriceVersionId!.Value,
                x.Value.CorrectUnitCostCents, x.Value.BaselineCutAt!.Value,
                CatalogCostBaselineOrigins.ApprovedRetroactiveBaseline)).ToArray()
            : [];
        var report = new FinancialCostCorrectionReport(
            rangeFrom,
            rangeToExclusive,
            skuReports,
            manifest,
            pending,
            Coverage(beforeResolvedUnits, totalUnits),
            Coverage(afterResolvedUnits, totalUnits),
            Coverage(financialResolved, financialTotal),
            manifest.Sum(x => x.ProfitImpactCents),
            manifest.Count,
            request.PlanType,
            baselineReports,
            historyCoverage);

        var canonicalScope = CanonicalScope(tenantId, clientId, request.SellerId, request.Reason.Trim(),
            rangeFrom, rangeToExclusive, request.PlanType, corrections, manifest, pending, historyCoverage);
        var hash = Sha256(canonicalScope);
        var existing = await _db.FinancialCorrectionPlans.SingleOrDefaultAsync(x => x.TenantId == tenantId
            && x.ClientId == clientId && x.SellerId == request.SellerId && x.PlanHash == hash, cancellationToken);
        if (existing == null)
        {
            existing = new FinancialCorrectionPlan
            {
                TenantId = tenantId,
                ClientId = clientId,
                SellerId = request.SellerId,
                PlanType = request.PlanType,
                Status = FinancialCorrectionPlanStatuses.DryRun,
                PlanHash = hash,
                ScopeJson = canonicalScope,
                ReportJson = JsonSerializer.Serialize(report),
                Reason = request.Reason.Trim(),
                TotalEntries = manifest.Count,
                CreatedByUserId = actorUserId
            };
            _db.FinancialCorrectionPlans.Add(existing);
            if (isBaselinePlan)
            {
                foreach (var rule in corrections.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    _db.CatalogCostBaselines.Add(new CatalogCostBaseline
                    {
                        PlanId = existing.Id,
                        ProductSku = rule.Value.ProductSku!,
                        VariantSku = rule.Key,
                        BaselinePriceVersionId = rule.Value.BaselinePriceVersionId!.Value,
                        BaselineUnitCostCents = rule.Value.CorrectUnitCostCents,
                        BaselineCutAt = rule.Value.BaselineCutAt!.Value,
                        Status = CatalogCostBaselineStatuses.Staged,
                        ApprovedByUserId = actorUserId,
                        Reason = request.Reason.Trim(),
                        PlanHash = hash
                    });
                }
            }
            await _db.SaveChangesAsync(cancellationToken);
        }

        return new FinancialCostCorrectionDryRunResult(existing.Id, hash, report, existing.Status);
    }

    private async Task<Dictionary<string, CorrectionRule>> BuildBaselineRulesAsync(
        FinancialCostCorrectionDryRunRequest request, CancellationToken cancellationToken)
    {
        var requested = request.Skus.ToDictionary(x => Sku.Normalize(x.Sku), x => x, StringComparer.Ordinal);
        var skus = requested.Keys.ToArray();
        var variants = await _db.ProductVariants.AsNoTracking()
            .Where(x => skus.Contains(x.VariantSku)).ToListAsync(cancellationToken);
        var versions = await _db.ProductPriceVersions.AsNoTracking()
            .Where(x => x.VariantSku != null && skus.Contains(x.VariantSku))
            .OrderBy(x => x.VariantSku).ThenBy(x => x.Version).ToListAsync(cancellationToken);
        var activeBaselines = await _db.CatalogCostBaselines.AsNoTracking()
            .Where(x => skus.Contains(x.VariantSku) && x.Status == CatalogCostBaselineStatuses.Active)
            .ToListAsync(cancellationToken);
        var rules = new Dictionary<string, CorrectionRule>(StringComparer.Ordinal);

        foreach (var pair in requested.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var variant = variants.SingleOrDefault(x => x.VariantSku == pair.Key)
                ?? throw new InvalidOperationException($"SKU interno {pair.Key} não possui variação cadastrada.");
            if (!variant.IsActive || variant.CatalogCostStatus != CatalogCostStatuses.Resolved
                                  || variant.CatalogPriceCents <= 0)
                throw new InvalidOperationException($"SKU {pair.Key} não possui custo interno atual aprovado.");
            if (variant.CatalogPriceOrigin is not (CatalogPriceOrigins.MasterProduct or CatalogPriceOrigins.VariantOverride))
                throw new InvalidOperationException($"SKU {pair.Key} possui origem de custo incompatível com baseline de catálogo.");

            var open = versions.Where(x => x.VariantSku == pair.Key && x.ValidTo == null).ToArray();
            if (open.Length != 1)
                throw new InvalidOperationException($"SKU {pair.Key} deve possuir exatamente uma versão de preço vigente.");
            var current = open[0];
            if (current.CatalogCostStatus != CatalogCostStatuses.Resolved
                || current.CatalogPriceCents != variant.CatalogPriceCents
                || current.CatalogPriceOrigin != variant.CatalogPriceOrigin
                || current.ProductSku != variant.BaseSku)
                throw new InvalidOperationException($"SKU {pair.Key} diverge da versão de preço vigente; revise o catálogo antes do baseline.");
            if (pair.Value.CorrectUnitCostCents > 0 && pair.Value.CorrectUnitCostCents != current.CatalogPriceCents)
                throw new InvalidOperationException($"O custo solicitado para {pair.Key} diverge do custo interno vigente aprovado.");

            var cut = (pair.Value.BaselineCutAt ?? current.ValidFrom).ToUniversalTime();
            if (cut == default || cut > DateTimeOffset.UtcNow)
                throw new InvalidOperationException($"O corte do baseline de {pair.Key} deve ser uma data passada válida.");
            var priorActive = activeBaselines.SingleOrDefault(x => x.VariantSku == pair.Key);
            rules[pair.Key] = new CorrectionRule(current.CatalogPriceCents, [], variant.BaseSku,
                current.Id, cut, priorActive?.Id, priorActive == null ? null : HashBaseline(priorActive));
        }

        return rules;
    }

    private async Task<FinancialCorrectionHistoryCoverage> RequireCompleteHistoryAsync(
        string tenantId, Guid clientId, long sellerId, CancellationToken cancellationToken)
    {
        var parent = await _db.FinancialSyncJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientId == clientId && x.SellerId == sellerId
                        && x.JobType == FinancialSyncJobTypes.OperationalSyncBatch
                        && x.DedupeKey.StartsWith("OP:HISTORY:"))
            .OrderByDescending(x => x.RangeTo).ThenByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("O dry-run do baseline exige sincronização histórica reconciliada.");
        var children = await _db.FinancialSyncJobs.AsNoTracking()
            .Where(x => x.ParentJobId == parent.Id).OrderBy(x => x.RangeFrom).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var windows = children.Where(x => x.JobType == FinancialSyncJobTypes.OperationalSyncChunk).ToArray();
        var isCompleteStatus = parent.Status is "COMPLETED" or "CURRENT";
        var coversTwelveMonths = parent.RangeFrom <= parent.RangeTo.AddMonths(-12);
        // PARTIAL means the search window itself finished; any unavailable ID is
        // represented explicitly and must be resolved by a gap-retry before the
        // parent can become CURRENT.
        var completedWindows = windows.Count(x => x.Status is "COMPLETED" or "CURRENT" or "PARTIAL");
        var unresolvedGaps = CountUnresolvedHistoryGaps(children);
        if (!isCompleteStatus || !coversTwelveMonths || windows.Length == 0
            || completedWindows != windows.Length || unresolvedGaps != 0)
            throw new InvalidOperationException(
                "O dry-run do baseline foi bloqueado: o histórico do seller ainda está parcial ou possui gaps não resolvidos.");

        var snapshot = JsonSerializer.Serialize(new
        {
            parent.Id,
            parent.Status,
            parent.RangeFrom,
            parent.RangeTo,
            parent.Total,
            parent.Processed,
            windows = children.Select(x => new { x.Id, x.RangeFrom, x.RangeTo, x.Status, x.Checkpoint, x.ResultJson })
        });
        return new FinancialCorrectionHistoryCoverage(parent.Id, parent.Status, parent.RangeFrom, parent.RangeTo,
            completedWindows, windows.Length, unresolvedGaps, Sha256(snapshot));
    }

    private static int CountUnresolvedHistoryGaps(IEnumerable<FinancialSyncJob> jobs)
    {
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        var anonymousGapCount = 0;
        foreach (var job in jobs.OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id))
        {
            if (string.IsNullOrWhiteSpace(job.ResultJson)) continue;
            try
            {
                using var json = JsonDocument.Parse(job.ResultJson);
                var root = json.RootElement;
                if ((root.TryGetProperty("orders", out var orders) || root.TryGetProperty("Orders", out orders))
                    && orders.ValueKind == JsonValueKind.Object)
                {
                    foreach (var order in orders.EnumerateObject())
                        states[order.Name] = order.Value.GetString() ?? "GAP";
                }
                else if ((root.TryGetProperty("unresolvedGaps", out var gaps)
                          || root.TryGetProperty("UnresolvedGaps", out gaps))
                         && gaps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var gap in gaps.EnumerateArray())
                    {
                        if (gap.ValueKind == JsonValueKind.Object
                            && (gap.TryGetProperty("orderId", out var id) || gap.TryGetProperty("OrderId", out id))
                            && !string.IsNullOrWhiteSpace(id.GetString()))
                            states[id.GetString()!] = "GAP";
                        else anonymousGapCount++;
                    }
                }
            }
            catch (JsonException)
            {
                anonymousGapCount++;
            }
        }
        return anonymousGapCount + states.Count(x => string.Equals(x.Value, "GAP", StringComparison.Ordinal));
    }

    public async Task<FinancialCostCorrectionPlanResult?> GetAsync(string tenantId, Guid clientId, Guid planId,
        CancellationToken cancellationToken = default)
    {
        var plan = await _db.FinancialCorrectionPlans.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == planId && x.TenantId == tenantId && x.ClientId == clientId, cancellationToken);
        if (plan == null) return null;
        var report = JsonSerializer.Deserialize<FinancialCostCorrectionReport>(plan.ReportJson)
            ?? throw new InvalidOperationException("Relatório de correção inválido.");
        return new FinancialCostCorrectionPlanResult(plan.Id, plan.PlanHash, plan.Status, report, plan.CreatedAt, plan.UpdatedAt);
    }

    public async Task<FinancialCostCorrectionPlanResult> ApproveAsync(string tenantId, Guid clientId, Guid planId,
        FinancialCorrectionPlanCommand command, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var plan = await RequirePlanAsync(tenantId, clientId, planId, cancellationToken);
        ValidateCommand(plan, command, actorUserId);
        if (plan.Status == FinancialCorrectionPlanStatuses.Stale)
            throw new InvalidOperationException("Plano STALE exige novo dry-run.");
        if (plan.Status != FinancialCorrectionPlanStatuses.DryRun && plan.Status != FinancialCorrectionPlanStatuses.AwaitingApproval)
            throw new InvalidOperationException($"Plano no estado {plan.Status} não pode ser aprovado.");
        plan.Status = FinancialCorrectionPlanStatuses.Preparing;
        plan.StartedAt = DateTimeOffset.UtcNow;
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        AddAudit(plan, actorUserId, "FinancialCorrectionPlan.Approve", command.Reason);
        await _db.SaveChangesAsync(cancellationToken);
        return (await GetAsync(tenantId, clientId, planId, cancellationToken))!;
    }

    public async Task<FinancialCostCorrectionPlanResult> ResumeAsync(string tenantId, Guid clientId, Guid planId,
        FinancialCorrectionPlanCommand command, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var plan = await RequirePlanAsync(tenantId, clientId, planId, cancellationToken);
        ValidateCommand(plan, command, actorUserId);
        if (plan.Status == FinancialCorrectionPlanStatuses.Stale)
            throw new InvalidOperationException("Plano STALE não pode ser retomado; execute novo dry-run.");
        if (plan.Status == FinancialCorrectionPlanStatuses.Completed)
            return (await GetAsync(tenantId, clientId, planId, cancellationToken))!;

        if (plan.Status is FinancialCorrectionPlanStatuses.Preparing or FinancialCorrectionPlanStatuses.Failed)
            await PrepareNextChunkAsync(plan, cancellationToken);
        else if (plan.Status == FinancialCorrectionPlanStatuses.PendingActivation)
            return (await GetAsync(tenantId, clientId, planId, cancellationToken))!;
        else if (plan.Status == FinancialCorrectionPlanStatuses.Reconciling)
            await ReconcileAsync(plan, cancellationToken);
        else
            throw new InvalidOperationException($"Plano no estado {plan.Status} não pode ser retomado.");

        return (await GetAsync(tenantId, clientId, planId, cancellationToken))!;
    }

    public async Task<FinancialCostCorrectionPlanResult> ActivateAsync(string tenantId, Guid clientId, Guid planId,
        FinancialCorrectionPlanCommand command, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var plan = await RequirePlanAsync(tenantId, clientId, planId, cancellationToken);
        ValidateCommand(plan, command, actorUserId);
        if (plan.Status == FinancialCorrectionPlanStatuses.Stale)
            throw new InvalidOperationException("Plano STALE não pode ser ativado; execute novo dry-run.");
        if (plan.Status != FinancialCorrectionPlanStatuses.PendingActivation)
            throw new InvalidOperationException($"Plano no estado {plan.Status} não pode ser ativado.");
        AddAudit(plan, actorUserId, "FinancialCorrectionPlan.Activate", command.Reason);
        await _db.SaveChangesAsync(cancellationToken);
        await ActivateInternalAsync(plan, cancellationToken);
        return (await GetAsync(tenantId, clientId, planId, cancellationToken))!;
    }

    private async Task PrepareNextChunkAsync(FinancialCorrectionPlan plan, CancellationToken cancellationToken)
    {
        var report = JsonSerializer.Deserialize<FinancialCostCorrectionReport>(plan.ReportJson)
            ?? throw new InvalidOperationException("Relatório de correção inválido.");
        var preparedIds = await _db.FinancialCorrectionPlanEntries.AsNoTracking()
            .Where(x => x.PlanId == plan.Id).Select(x => x.OriginalEntryId).ToListAsync(cancellationToken);
        var prepared = preparedIds.ToHashSet();
        var chunk = report.Manifest.Where(x => !prepared.Contains(x.ExpectedActiveEntryId))
            .OrderBy(x => x.EconomicKey, StringComparer.Ordinal).Take(PreparationChunkSize).ToArray();

        foreach (var manifest in chunk)
        {
            // Staging lives only in the correction manifest. Creating a financial
            // ledger row here would leak into history/divergence queries before the
            // final CAS activation, even if it was not the active head.
            _db.FinancialCorrectionPlanEntries.Add(new FinancialCorrectionPlanEntry
            {
                PlanId = plan.Id, OriginalEntryId = manifest.ExpectedActiveEntryId, ReplacementEntryId = null,
                MarketplaceOrderId = manifest.MarketplaceOrderId, EconomicKey = manifest.EconomicKey,
                State = FinancialCorrectionEntryStates.Staged,
                ExpectedHeadId = manifest.ExpectedHeadId, ExpectedActiveEntryId = manifest.ExpectedActiveEntryId,
                ExpectedHeadVersion = manifest.ExpectedHeadVersion, ExpectedActiveEntryHash = manifest.ExpectedActiveEntryHash,
                ExpectedPriceVersionId = manifest.ExpectedPriceVersionId,
                ExpectedCostReferencesHash = manifest.ExpectedCostReferencesHash,
                CurrentAmountCents = -manifest.CurrentCostCents,
                ReplacementAmountCents = -manifest.ReplacementCostCents,
                ReplacementBreakdownJson = manifest.ReplacementBreakdownJson
            });
        }

        if (chunk.Length > 0)
            await _db.SaveChangesAsync(cancellationToken);

        plan.ProcessedEntries = prepared.Count + chunk.Length;
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        if (plan.ProcessedEntries >= plan.TotalEntries)
        {
            plan.Status = FinancialCorrectionPlanStatuses.PendingActivation;
            var staged = await _db.FinancialCorrectionPlanEntries.Where(x => x.PlanId == plan.Id).ToListAsync(cancellationToken);
            foreach (var entry in staged) entry.State = FinancialCorrectionEntryStates.PendingActivation;
        }
        else plan.Status = FinancialCorrectionPlanStatuses.Preparing;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task ActivateInternalAsync(FinancialCorrectionPlan plan, CancellationToken cancellationToken)
    {
        var rows = await _db.FinancialCorrectionPlanEntries.Where(x => x.PlanId == plan.Id)
            .OrderBy(x => x.EconomicKey).ToListAsync(cancellationToken);
        if (rows.Count != plan.TotalEntries || rows.Any(x => x.State != FinancialCorrectionEntryStates.PendingActivation
                                                             || x.ReplacementEntryId.HasValue))
            throw new InvalidOperationException("O manifesto preparado está incompleto ou já foi ativado.");
        var ownsTransaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction == null;
        await using var transaction = ownsTransaction ? await _db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            plan.Status = FinancialCorrectionPlanStatuses.Activating;
            if (plan.PlanType == FinancialCorrectionPlanTypes.CatalogBaselineCurrent)
            {
                var report = JsonSerializer.Deserialize<FinancialCostCorrectionReport>(plan.ReportJson)
                    ?? throw new InvalidOperationException("Relatório de correção inválido.");
                var currentCoverage = await RequireCompleteHistoryAsync(
                    plan.TenantId, plan.ClientId, plan.SellerId, cancellationToken);
                if (report.HistoryCoverage == null
                    || report.HistoryCoverage.SnapshotHash != currentCoverage.SnapshotHash)
                    throw new StaleCorrectionPlanException();
            }
            var stagedBaselines = plan.PlanType == FinancialCorrectionPlanTypes.CatalogBaselineCurrent
                ? await _db.CatalogCostBaselines.Where(x => x.PlanId == plan.Id)
                    .OrderBy(x => x.VariantSku).ToListAsync(cancellationToken)
                : [];
            var baselineExpectations = plan.PlanType == FinancialCorrectionPlanTypes.CatalogBaselineCurrent
                ? ReadBaselineExpectations(plan.ScopeJson)
                : new Dictionary<string, BaselineExpectation>(StringComparer.Ordinal);
            if (plan.PlanType == FinancialCorrectionPlanTypes.CatalogBaselineCurrent
                && (stagedBaselines.Count != baselineExpectations.Count
                    || stagedBaselines.Any(x => x.Status != CatalogCostBaselineStatuses.Staged)))
                throw new StaleCorrectionPlanException();

            foreach (var baseline in stagedBaselines)
            {
                if (!baselineExpectations.TryGetValue(baseline.VariantSku, out var expected))
                    throw new StaleCorrectionPlanException();
                var version = await _db.ProductPriceVersions.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == baseline.BaselinePriceVersionId, cancellationToken);
                var variant = await _db.ProductVariants.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.VariantSku == baseline.VariantSku, cancellationToken);
                if (version == null || variant == null || version.ValidTo != null
                    || version.CatalogPriceCents != baseline.BaselineUnitCostCents
                    || variant.CatalogPriceCents != baseline.BaselineUnitCostCents
                    || variant.CatalogCostStatus != CatalogCostStatuses.Resolved)
                    throw new StaleCorrectionPlanException();
                var current = await LoadActiveBaselineForUpdateAsync(baseline.VariantSku, cancellationToken);
                if (current?.Id != expected.ExpectedActiveBaselineId
                    || current != null && HashBaseline(current) != expected.ExpectedActiveBaselineHash)
                    throw new StaleCorrectionPlanException();
            }

            foreach (var row in rows)
            {
                var head = await LockHeadAsync(row.ExpectedHeadId, cancellationToken);
                var active = await _db.MarketplaceFinancialEntries.AsNoTracking()
                    .SingleAsync(x => x.Id == head.ActiveEntryId, cancellationToken);
                var item = await _db.MarketplaceOrderItems.AsNoTracking()
                    .SingleAsync(x => x.Id == active.MarketplaceOrderItemId, cancellationToken);
                if (head.ActiveEntryId != row.ExpectedActiveEntryId || head.Version != row.ExpectedHeadVersion
                    || HashActiveEntry(active) != row.ExpectedActiveEntryHash
                    || item.CatalogPriceVersionId != row.ExpectedPriceVersionId
                    || Sha256(CanonicalReferences(ParseCostReferences(item.CostReferencesJson))) != row.ExpectedCostReferencesHash)
                    throw new StaleCorrectionPlanException();
            }

            var activationTime = DateTimeOffset.UtcNow;
            var supersededAnyBaseline = false;
            foreach (var baseline in stagedBaselines)
            {
                var current = await _db.CatalogCostBaselines.SingleOrDefaultAsync(x =>
                    x.VariantSku == baseline.VariantSku && x.Status == CatalogCostBaselineStatuses.Active,
                    cancellationToken);
                if (current != null)
                {
                    current.Status = CatalogCostBaselineStatuses.Discarded;
                    supersededAnyBaseline = true;
                }
            }
            // The partial unique index is immediate in PostgreSQL. Flush the old
            // ACTIVE rows first, still inside this transaction, before promoting
            // the staged replacements.
            if (supersededAnyBaseline) await _db.SaveChangesAsync(cancellationToken);
            foreach (var baseline in stagedBaselines)
            {
                baseline.Status = CatalogCostBaselineStatuses.Active;
                baseline.ApprovedAt = activationTime;
                baseline.ActivatedAt = activationTime;
            }

            foreach (var row in rows)
            {
                var head = await _db.FinancialEconomicHeads.SingleAsync(x => x.Id == row.ExpectedHeadId, cancellationToken);
                var original = await _db.MarketplaceFinancialEntries.AsNoTracking()
                    .SingleAsync(x => x.Id == row.ExpectedActiveEntryId, cancellationToken);
                var metadata = JsonSerializer.Serialize(new
                {
                    correctionPlanId = plan.Id,
                    correctionPlanHash = plan.PlanHash,
                    originalEntryId = original.Id,
                    row.ExpectedHeadVersion,
                    row.ExpectedPriceVersionId,
                    replacementBreakdown = JsonDocument.Parse(row.ReplacementBreakdownJson).RootElement,
                    reason = plan.Reason
                });
                var replacement = new MarketplaceFinancialEntry
                {
                    TenantId = original.TenantId,
                    ClientId = original.ClientId,
                    Provider = original.Provider,
                    SellerId = original.SellerId,
                    EntryType = original.EntryType,
                    Layer = original.Layer,
                    Status = original.Status,
                    AmountCents = row.ReplacementAmountCents,
                    CurrencyId = original.CurrencyId,
                    EconomicKey = original.EconomicKey,
                    IdempotencyKey = $"CORRECTION:{plan.Id:N}:{original.Id:N}",
                    SupersedesEntryId = original.Id,
                    MarketplaceOrderId = original.MarketplaceOrderId,
                    MarketplaceOrderItemId = original.MarketplaceOrderItemId,
                    ExternalOrderId = original.ExternalOrderId,
                    ExternalPaymentId = original.ExternalPaymentId,
                    ExternalShipmentId = original.ExternalShipmentId,
                    ExternalPackId = original.ExternalPackId,
                    ExternalClaimId = original.ExternalClaimId,
                    ExternalReturnId = original.ExternalReturnId,
                    EconomicOccurredAt = original.EconomicOccurredAt,
                    FinancialConfirmedAt = original.FinancialConfirmedAt,
                    ProviderUpdatedAt = original.ProviderUpdatedAt,
                    ObservedAt = DateTimeOffset.UtcNow,
                    SourceEndpoint = "financial-corrections/activate",
                    SourceRecordId = plan.Id.ToString("N"),
                    CanonicalPayloadHash = Sha256(metadata),
                    MetadataJson = metadata
                };
                _db.MarketplaceFinancialEntries.Add(replacement);
                row.ReplacementEntryId = replacement.Id;
                head.ActiveEntryId = replacement.Id;
                head.Version++;
                head.UpdatedAt = DateTimeOffset.UtcNow;
                row.State = FinancialCorrectionEntryStates.Active;
                if (original.MarketplaceOrderItemId.HasValue)
                {
                    var item = await _db.MarketplaceOrderItems.SingleAsync(
                        x => x.Id == original.MarketplaceOrderItemId.Value, cancellationToken);
                    item.ProductCostEntryId = replacement.Id;
                    if (plan.PlanType == FinancialCorrectionPlanTypes.CatalogBaselineCurrent)
                    {
                        var baseline = stagedBaselines.Single(x => x.VariantSku == item.SabrVariantSku);
                        item.CatalogCostBaselineId = baseline.Id;
                    }
                }
            }
            plan.Status = FinancialCorrectionPlanStatuses.Reconciling;
            plan.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
        }
        catch (StaleCorrectionPlanException)
        {
            if (transaction != null) await transaction.RollbackAsync(cancellationToken);
            if (_db is DbContext context) context.ChangeTracker.Clear();
            var stale = await RequirePlanAsync(plan.TenantId, plan.ClientId, plan.Id, cancellationToken);
            stale.Status = FinancialCorrectionPlanStatuses.Stale;
            stale.LastError = "A cabeça financeira mudou depois do dry-run; um novo dry-run é obrigatório.";
            stale.UpdatedAt = DateTimeOffset.UtcNow;
            var staged = await _db.FinancialCorrectionPlanEntries.Where(x => x.PlanId == plan.Id).ToListAsync(cancellationToken);
            foreach (var entry in staged) entry.State = FinancialCorrectionEntryStates.Discarded;
            var baselines = await _db.CatalogCostBaselines.Where(x => x.PlanId == plan.Id).ToListAsync(cancellationToken);
            foreach (var baseline in baselines) baseline.Status = CatalogCostBaselineStatuses.Discarded;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        await ReconcileAsync(plan, cancellationToken);
    }

    private async Task ReconcileAsync(FinancialCorrectionPlan plan, CancellationToken cancellationToken)
    {
        if (_projection == null)
            throw new InvalidOperationException("Serviço de projeção é obrigatório para concluir a reconciliação.");
        var orderIds = await _db.FinancialCorrectionPlanEntries.AsNoTracking().Where(x => x.PlanId == plan.Id)
            .Where(x => x.MarketplaceOrderId.HasValue).Select(x => x.MarketplaceOrderId!.Value).Distinct().ToListAsync(cancellationToken);
        foreach (var orderId in orderIds.Order())
        {
            var order = await _db.MarketplaceOrders.Include(x => x.Items).SingleAsync(x => x.Id == orderId, cancellationToken);
            await _projection.RebuildOrderStateAsync(order, cancellationToken);
        }
        plan.Status = FinancialCorrectionPlanStatuses.Completed;
        plan.CompletedAt = DateTimeOffset.UtcNow;
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<FinancialEconomicHead> LockHeadAsync(Guid headId, CancellationToken cancellationToken)
    {
        if (_db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            return await _db.FinancialEconomicHeads.FromSqlInterpolated(
                $"SELECT * FROM financial_economic_heads WHERE id = {headId} FOR UPDATE").SingleAsync(cancellationToken);
        return await _db.FinancialEconomicHeads.SingleAsync(x => x.Id == headId, cancellationToken);
    }

    private async Task<CatalogCostBaseline?> LoadActiveBaselineForUpdateAsync(
        string variantSku, CancellationToken cancellationToken)
    {
        if (_db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            return await _db.CatalogCostBaselines.FromSqlInterpolated(
                $"SELECT * FROM catalog_cost_baselines WHERE variant_sku = {variantSku} AND status = 'ACTIVE' FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
        return await _db.CatalogCostBaselines.SingleOrDefaultAsync(x =>
            x.VariantSku == variantSku && x.Status == CatalogCostBaselineStatuses.Active, cancellationToken);
    }

    private static Dictionary<string, BaselineExpectation> ReadBaselineExpectations(string scopeJson)
    {
        using var document = JsonDocument.Parse(scopeJson);
        var result = new Dictionary<string, BaselineExpectation>(StringComparer.Ordinal);
        foreach (var correction in document.RootElement.GetProperty("corrections").EnumerateArray())
        {
            var sku = correction.GetProperty("sku").GetString()
                ?? throw new InvalidOperationException("Manifesto de baseline sem SKU.");
            Guid? id = null;
            if (correction.TryGetProperty("ExpectedActiveBaselineId", out var idElement)
                && idElement.ValueKind == JsonValueKind.String)
                id = idElement.GetGuid();
            string? hash = null;
            if (correction.TryGetProperty("ExpectedActiveBaselineHash", out var hashElement)
                && hashElement.ValueKind == JsonValueKind.String)
                hash = hashElement.GetString();
            result.Add(sku, new BaselineExpectation(id, hash));
        }
        return result;
    }

    private static void ValidateCommand(FinancialCorrectionPlan plan, FinancialCorrectionPlanCommand command, Guid actorUserId)
    {
        if (actorUserId == Guid.Empty || string.IsNullOrWhiteSpace(command.Reason))
            throw new ArgumentException("Ator e motivo são obrigatórios.");
        if (!string.Equals(plan.PlanHash, command.PlanHash?.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("planHash divergente; execute um novo dry-run.");
    }

    private void AddAudit(FinancialCorrectionPlan plan, Guid actorUserId, string action, string reason)
    {
        _db.AuditEvents.Add(new AuditEvent
        {
            TenantId = plan.TenantId,
            ActorType = "AdminUser",
            ActorId = actorUserId,
            Action = action,
            Entity = nameof(FinancialCorrectionPlan),
            EntityId = plan.Id,
            RequestId = Guid.NewGuid(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                plan.PlanHash,
                plan.SellerId,
                reason = reason.Trim()
            })
        });
    }

    private async Task<FinancialCorrectionPlan> RequirePlanAsync(string tenantId, Guid clientId, Guid planId,
        CancellationToken cancellationToken) => await _db.FinancialCorrectionPlans.SingleOrDefaultAsync(x =>
            x.Id == planId && x.TenantId == tenantId && x.ClientId == clientId, cancellationToken)
        ?? throw new KeyNotFoundException("Plano de correção não encontrado.");

    private static Evaluation Evaluate(CandidateRow row, CorrectionRule rule,
        IReadOnlyCollection<ProductPriceVersion> versions, bool isBaselinePlan)
    {
        FinancialCostCorrectionPendingItem Pending(string code, string detail) => new(
            row.Head.EconomicKey, row.Order.Id, row.Item.Id, row.Item.SabrVariantSku, code, detail,
            row.Item.CatalogPriceVersionId, row.Item.CostSource, Math.Abs(row.Entry.AmountCents));

        var economicAt = row.Order.PaidAt ?? row.Order.ChannelCreatedAt;
        var economicAtSource = row.Order.PaidAt.HasValue ? "PAID_AT" : row.Order.ChannelCreatedAt.HasValue ? "CHANNEL_CREATED_AT" : null;
        if (!economicAt.HasValue || economicAtSource == null)
            return new(null, Pending("ECONOMIC_AT_PENDING", "Pedido sem PaidAt e ChannelCreatedAt."));
        economicAt = economicAt.Value.ToUniversalTime();
        if (row.Item.EconomicAt.HasValue && row.Item.EconomicAt.Value.ToUniversalTime() != economicAt.Value)
            return new(null, Pending("ECONOMIC_AT_AMBIGUOUS", "EconomicAt do item diverge da data econômica do pedido."));
        if (row.Entry.EconomicOccurredAt.ToUniversalTime() != economicAt.Value)
            return new(null, Pending("ECONOMIC_AT_AMBIGUOUS", "Data do fato financeiro diverge da data econômica do pedido."));
        if (isBaselinePlan && economicAt.Value >= rule.BaselineCutAt!.Value)
            return new(null, null);

        if (row.Item.CostSource is not ("CATALOG_PRICE" or "MIXED"))
            return new(null, Pending("COST_SOURCE_NOT_ELIGIBLE", $"Origem {row.Item.CostSource ?? "ausente"} não é corrigível por preço de catálogo."));

        CostReference[] references;
        try { references = ParseCostReferences(row.Item.CostReferencesJson); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new(null, Pending("COST_REFERENCES_INVALID", ex.Message));
        }
        if (references.Length == 0)
            return new(null, Pending("COST_REFERENCES_PENDING", "Item sem composição histórica de custo."));
        if (references.Any(x => x.Quantity <= 0 || x.UnitCostCents <= 0))
            return new(null, Pending("COST_REFERENCES_INVALID", "Quantidade e custo devem ser positivos em todas as parcelas."));
        if (references.Sum(x => x.Quantity) != row.Item.Quantity)
            return new(null, Pending("COST_QUANTITY_AMBIGUOUS", "A soma das parcelas não coincide com a quantidade do item."));

        var catalog = references.Where(x => x.Source == "GENERAL_STOCK").ToArray();
        var lots = references.Where(x => x.Source == "PREPURCHASED_LOT").ToArray();
        if (catalog.Length + lots.Length != references.Length || catalog.Length == 0)
            return new(null, Pending("COST_SOURCE_AMBIGUOUS", "A composição contém origem desconhecida ou não possui parcela de catálogo."));
        if (row.Item.CostSource == "CATALOG_PRICE" && lots.Length != 0 || row.Item.CostSource == "MIXED" && lots.Length == 0)
            return new(null, Pending("COST_SOURCE_AMBIGUOUS", "CostSource não coincide com a composição persistida."));

        var catalogVersionIds = catalog.Select(x => x.CatalogPriceVersionId).Distinct().ToArray();
        if (catalogVersionIds.Length != 1 || !catalogVersionIds[0].HasValue || row.Item.CatalogPriceVersionId != catalogVersionIds[0])
            return new(null, Pending("PRICE_VERSION_AMBIGUOUS", "Parcela de catálogo não referencia uma única versão coerente."));
        var versionId = catalogVersionIds[0]!.Value;
        if (!isBaselinePlan && !rule.IncorrectVersionIds.Contains(versionId))
            return new(null, Pending("PRICE_VERSION_NOT_AUTHORIZED", "A versão de preço não pertence ao conjunto incorreto autorizado."));

        var referencedVersions = versions.Where(x => x.VariantSku == row.Item.SabrVariantSku && x.Id == versionId).ToArray();
        if (referencedVersions.Length != 1)
            return new(null, Pending("PRICE_VERSION_AMBIGUOUS", "A versão histórica referenciada não pertence inequivocamente ao SKU."));
        if (!isBaselinePlan)
        {
            var effectiveVersions = versions.Where(x => x.VariantSku == row.Item.SabrVariantSku
                && x.ValidFrom <= economicAt.Value && (x.ValidTo == null || economicAt.Value < x.ValidTo.Value)).ToArray();
            if (effectiveVersions.Length != 1 || effectiveVersions[0].Id != versionId)
                return new(null, Pending("PRICE_VERSION_AMBIGUOUS", "A versão referenciada não é a única vigente em economicAt."));
        }

        var catalogQuantity = catalog.Sum(x => x.Quantity);
        var lotQuantity = lots.Sum(x => x.Quantity);
        var preservedLotCost = lots.Sum(x => checked(x.UnitCostCents * x.Quantity));
        var replacementCost = checked(preservedLotCost + rule.CorrectUnitCostCents * catalogQuantity);
        var currentCost = checked(-row.Entry.AmountCents);
        if (currentCost <= 0)
            return new(null, Pending("ACTIVE_COST_INVALID", "A cabeça PRODUCT_COST não contém débito negativo válido."));

        var canonicalReferences = CanonicalReferences(references);
        var referencesHash = Sha256(canonicalReferences);
        var activeEntryHash = HashActiveEntry(row.Entry);
        var breakdown = JsonSerializer.Serialize(new
        {
            catalog = new
            {
                quantity = catalogQuantity,
                unitCostCents = rule.CorrectUnitCostCents,
                previousPriceVersionId = versionId,
                baselinePriceVersionId = rule.BaselinePriceVersionId,
                origin = isBaselinePlan ? CatalogCostBaselineOrigins.ApprovedRetroactiveBaseline : referencedVersions[0].CatalogPriceOrigin,
                baselineCutAt = rule.BaselineCutAt
            },
            prePurchasedLots = lots.OrderBy(x => x.LotId).Select(x => new { x.LotId, x.Quantity, x.UnitCostCents })
        });

        return new(new FinancialCostCorrectionManifestEntry(
            row.Head.EconomicKey,
            row.Head.Id,
            row.Head.ActiveEntryId,
            row.Head.Version,
            activeEntryHash,
            versionId,
            referencesHash,
            row.Order.Id,
            row.Item.Id,
            row.Item.SabrVariantSku!,
            economicAt.Value,
            economicAtSource,
            row.Item.CostSource!,
            catalogQuantity,
            lotQuantity,
            currentCost,
            replacementCost,
            currentCost - replacementCost,
            breakdown,
            rule.BaselineCutAt,
            rule.BaselinePriceVersionId), null);
    }

    private static CostReference[] ParseCostReferences(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("CostReferencesJson deve ser uma lista.");
        return document.RootElement.EnumerateArray().Select(element => new CostReference(
            element.GetProperty("source").GetString()?.Trim().ToUpperInvariant() ?? string.Empty,
            element.TryGetProperty("lotId", out var lot) && lot.ValueKind != JsonValueKind.Null ? lot.GetGuid() : null,
            element.GetProperty("quantity").GetInt32(),
            element.GetProperty("unitCostCents").GetInt64(),
            element.TryGetProperty("catalogPriceVersionId", out var version) && version.ValueKind != JsonValueKind.Null ? version.GetGuid() : null,
            element.TryGetProperty("priceOrigin", out var origin) ? origin.GetString() : null)).ToArray();
    }

    private static string CanonicalReferences(IEnumerable<CostReference> references) => JsonSerializer.Serialize(
        references.OrderBy(x => x.Source, StringComparer.Ordinal).ThenBy(x => x.LotId).ThenBy(x => x.CatalogPriceVersionId)
            .Select(x => new { x.Source, x.LotId, x.Quantity, x.UnitCostCents, x.CatalogPriceVersionId, x.PriceOrigin }));

    private static string HashActiveEntry(MarketplaceFinancialEntry entry) => Sha256(JsonSerializer.Serialize(new
    {
        entry.Id,
        entry.EconomicKey,
        entry.AmountCents,
        entry.CurrencyId,
        entry.Status,
        entry.Layer,
        entry.CanonicalPayloadHash,
        entry.SupersedesEntryId
    }));

    private static string HashBaseline(CatalogCostBaseline baseline) => Sha256(JsonSerializer.Serialize(new
    {
        baseline.Id,
        baseline.PlanId,
        baseline.ProductSku,
        baseline.VariantSku,
        baseline.BaselinePriceVersionId,
        baseline.BaselineUnitCostCents,
        baseline.BaselineCutAt,
        baseline.CostOrigin,
        baseline.Status,
        baseline.PlanHash
    }));

    private static string CanonicalScope(string tenantId, Guid clientId, long sellerId, string reason,
        DateTimeOffset rangeFrom, DateTimeOffset rangeToExclusive, string planType,
        IReadOnlyDictionary<string, CorrectionRule> corrections,
        IEnumerable<FinancialCostCorrectionManifestEntry> manifest,
        IEnumerable<FinancialCostCorrectionPendingItem> pending,
        FinancialCorrectionHistoryCoverage? historyCoverage) => JsonSerializer.Serialize(new
        {
            tenantId,
            clientId,
            sellerId,
            planType,
            rangeFrom,
            rangeToExclusive,
            reason,
            historyCoverage,
            corrections = corrections.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new
            {
                sku = x.Key,
                x.Value.CorrectUnitCostCents,
                incorrectCatalogPriceVersionIds = x.Value.IncorrectVersionIds,
                x.Value.ProductSku,
                x.Value.BaselinePriceVersionId,
                x.Value.BaselineCutAt,
                x.Value.ExpectedActiveBaselineId,
                x.Value.ExpectedActiveBaselineHash
            }),
            manifest = manifest.OrderBy(x => x.EconomicKey, StringComparer.Ordinal),
            pending = pending.OrderBy(x => x.EconomicKey, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal)
        });

    private static FinancialCorrectionCoverage Coverage(long resolved, long total) => new(
        resolved,
        total,
        total == 0 ? 0m : Math.Round(resolved * 100m / total, 2, MidpointRounding.AwayFromZero));

    private static bool IsCostResolved(MarketplaceOrderItem item, IReadOnlySet<Guid> productCostItemIds)
    {
        if (!productCostItemIds.Contains(item.Id)) return false;
        if (MarketplaceMappingStates.IsExternal(item.MappingState))
            return item.ExternalUnitCostCentsSnapshot is > 0 && item.ExternalCostVersionId.HasValue;
        if (string.IsNullOrWhiteSpace(item.SabrVariantSku) || string.IsNullOrWhiteSpace(item.CostSource)) return false;
        try
        {
            var references = ParseCostReferences(item.CostReferencesJson);
            return references.Length > 0
                   && references.All(x => x.Quantity > 0 && x.UnitCostCents > 0)
                   && references.Sum(x => x.Quantity) == item.Quantity;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void ValidateRequest(string tenantId, Guid clientId, FinancialCostCorrectionDryRunRequest request, Guid actorUserId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty || actorUserId == Guid.Empty)
            throw new ArgumentException("Tenant, cliente e ator são obrigatórios.");
        if (request.SellerId <= 0 || request.Skus.Count == 0 || string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("Seller, SKUs e motivo são obrigatórios.");
        if (request.PlanType is not (FinancialCorrectionPlanTypes.PriceVersionCorrection
            or FinancialCorrectionPlanTypes.CatalogBaselineCurrent))
            throw new ArgumentException($"Tipo de plano {request.PlanType} não suportado.");
        if (request.PlanType == FinancialCorrectionPlanTypes.PriceVersionCorrection
            && request.Skus.Any(x => x.CorrectUnitCostCents <= 0 || x.IncorrectCatalogPriceVersionIds.Count == 0))
            throw new ArgumentException("Cada SKU exige custo positivo e ao menos uma versão de preço incorreta comprovada.");
        if (request.PlanType == FinancialCorrectionPlanTypes.CatalogBaselineCurrent
            && request.Skus.Any(x => x.CorrectUnitCostCents < 0))
            throw new ArgumentException("O custo opcional do baseline não pode ser negativo.");
        var normalized = request.Skus.Select(x => Sku.Normalize(x.Sku)).ToArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new ArgumentException("Não é permitido repetir SKU no plano.");
    }

    private sealed record CandidateRow(FinancialEconomicHead Head, MarketplaceFinancialEntry Entry,
        MarketplaceOrderItem Item, MarketplaceOrder Order);
    private sealed record CorrectionRule(long CorrectUnitCostCents, Guid[] IncorrectVersionIds,
        string? ProductSku = null, Guid? BaselinePriceVersionId = null, DateTimeOffset? BaselineCutAt = null,
        Guid? ExpectedActiveBaselineId = null, string? ExpectedActiveBaselineHash = null);
    private sealed record CostReference(string Source, Guid? LotId, int Quantity, long UnitCostCents,
        Guid? CatalogPriceVersionId, string? PriceOrigin);
    private sealed record BaselineExpectation(Guid? ExpectedActiveBaselineId, string? ExpectedActiveBaselineHash);
    private sealed record Evaluation(FinancialCostCorrectionManifestEntry? Manifest, FinancialCostCorrectionPendingItem? Pending);
    private sealed class StaleCorrectionPlanException : Exception;
}
