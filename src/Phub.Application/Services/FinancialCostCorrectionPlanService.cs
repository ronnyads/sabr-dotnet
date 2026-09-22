using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Entities;

namespace Phub.Application.Services;

public sealed class FinancialCostCorrectionPlanService
{
    private readonly IAppDbContext _db;
    public FinancialCostCorrectionPlanService(IAppDbContext db) => _db = db;

    public async Task<FinancialCostCorrectionDryRunResult> DryRunAsync(string tenantId, Guid clientId,
        FinancialCostCorrectionDryRunRequest request, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        if (request.SellerId <= 0 || request.Skus.Count == 0 || string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("Seller, SKUs e motivo são obrigatórios.");
        var corrections = request.Skus.ToDictionary(x => Phub.Domain.ValueObjects.Sku.Normalize(x.Sku), x => x.CorrectUnitCostCents);
        if (corrections.Values.Any(x => x <= 0)) throw new ArgumentException("Custo correto deve ser positivo.");

        var itemRows = await (from head in _db.FinancialEconomicHeads.AsNoTracking()
                              join entry in _db.MarketplaceFinancialEntries.AsNoTracking() on head.ActiveEntryId equals entry.Id
                              join item in _db.MarketplaceOrderItems.AsNoTracking() on entry.MarketplaceOrderItemId equals item.Id
                              where head.TenantId == tenantId && head.ClientId == clientId && head.SellerId == request.SellerId
                                    && entry.EntryType == FinancialEntryTypes.ProductCost
                                    && item.SabrVariantSku != null && corrections.Keys.Contains(item.SabrVariantSku)
                                    && item.CostSource != "PREPURCHASED_LOT" && item.CostSource != "MIXED"
                              select new { Entry = entry, Item = item }).ToListAsync(cancellationToken);

        var reports = itemRows.GroupBy(x => x.Item.SabrVariantSku!, StringComparer.Ordinal).Select(group =>
        {
            var quantity = group.Sum(x => Math.Max(0, x.Item.Quantity));
            var current = -group.Sum(x => x.Entry.AmountCents);
            var correct = checked(corrections[group.Key] * quantity);
            return new FinancialCostCorrectionSkuReport(group.Key, quantity, current, correct,
                correct - current, current - correct, group.Count());
        }).OrderBy(x => x.Sku, StringComparer.Ordinal).ToList();

        var canonicalScope = JsonSerializer.Serialize(new
        {
            tenantId, clientId, request.SellerId,
            Skus = corrections.OrderBy(x => x.Key).Select(x => new { sku = x.Key, correctUnitCostCents = x.Value }),
            reason = request.Reason.Trim()
        });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalScope)));
        var existing = await _db.FinancialCorrectionPlans.SingleOrDefaultAsync(x => x.TenantId == tenantId
            && x.ClientId == clientId && x.SellerId == request.SellerId && x.PlanHash == hash, cancellationToken);
        if (existing == null)
        {
            existing = new FinancialCorrectionPlan
            {
                TenantId = tenantId, ClientId = clientId, SellerId = request.SellerId,
                PlanHash = hash, ScopeJson = canonicalScope, ReportJson = JsonSerializer.Serialize(reports),
                Reason = request.Reason.Trim(), TotalEntries = reports.Sum(x => x.EntriesImpacted),
                CreatedByUserId = actorUserId
            };
            _db.FinancialCorrectionPlans.Add(existing);
            await _db.SaveChangesAsync(cancellationToken);
        }
        return new FinancialCostCorrectionDryRunResult(existing.Id, hash, reports,
            reports.Sum(x => x.ProfitImpactCents), reports.Sum(x => x.EntriesImpacted), existing.Status);
    }
}
