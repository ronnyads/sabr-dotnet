using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Entities;

namespace Phub.Application.Services;

public sealed record LegacyFinancialCostRepairResult(
    int AccountsScanned,
    int EntriesReplaced,
    int ExternalCostsVoided,
    int OrdersRebuilt,
    long PreviousCostCents,
    long CorrectedCostCents,
    long ProfitImpactCents);

/// <summary>
/// One-time/idempotent repair for legacy product-cost heads created before cost
/// provenance was mandatory. It never mutates ledger facts: every correction is
/// appended and becomes the new economic head.
/// </summary>
public sealed class LegacyFinancialCostRepairService
{
    private const string RepairVersion = "LEGACY_PRODUCT_COST_V1";
    private readonly IAppDbContext _db;
    private readonly FinancialLedgerService _ledger;
    private readonly OperationalFinancialProjectionService _projection;
    private readonly HistoricalProductCostService _historicalCosts;

    public LegacyFinancialCostRepairService(IAppDbContext db, FinancialLedgerService ledger,
        OperationalFinancialProjectionService projection, HistoricalProductCostService historicalCosts)
    {
        _db = db;
        _ledger = ledger;
        _projection = projection;
        _historicalCosts = historicalCosts;
    }

    public async Task<LegacyFinancialCostRepairResult> RepairAllAsync(
        bool apply, CancellationToken cancellationToken = default)
    {
        var candidates = await (from head in _db.FinancialEconomicHeads.AsNoTracking()
                                join entry in _db.MarketplaceFinancialEntries.AsNoTracking()
                                    on head.ActiveEntryId equals entry.Id
                                join item in _db.MarketplaceOrderItems.AsNoTracking()
                                    on entry.MarketplaceOrderItemId equals item.Id
                                join order in _db.MarketplaceOrders.AsNoTracking()
                                    on item.MarketplaceOrderId equals order.Id
                                where entry.EntryType == FinancialEntryTypes.ProductCost
                                      && entry.Status != FinancialEntryStatuses.Voided
                                orderby entry.ClientId, entry.SellerId, entry.EconomicKey
                                select new Candidate(head.Id, entry, item, order)).ToListAsync(cancellationToken);

        var accounts = new HashSet<(string TenantId, Guid ClientId, long SellerId)>();
        var affectedOrders = new HashSet<Guid>();
        var replaced = 0;
        var voided = 0;
        long before = 0;
        long after = 0;

        foreach (var row in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            accounts.Add((row.Entry.TenantId, row.Entry.ClientId, row.Entry.SellerId));
            var externalWithoutCost = MarketplaceMappingStates.IsExternal(row.Item.MappingState)
                                      && !row.Item.ExternalUnitCostCentsSnapshot.HasValue;
            HistoricalProductCost? resolved = null;
            if (!externalWithoutCost && !string.IsNullOrWhiteSpace(row.Item.SabrVariantSku))
                resolved = await _historicalCosts.ResolveAsync(row.Order, row.Item, cancellationToken);

            var expected = resolved == null ? (long?)null : -checked(resolved.CatalogPriceCents * row.Item.Quantity);
            var metadataMissing = string.IsNullOrWhiteSpace(row.Item.CostSource);
            var needsInternalReplacement = expected.HasValue
                                           && (row.Entry.AmountCents != expected.Value || metadataMissing);
            if (!externalWithoutCost && !needsInternalReplacement) continue;

            var replacementAmount = externalWithoutCost ? row.Entry.AmountCents : expected!.Value;
            before += Math.Abs(row.Entry.AmountCents);
            if (!externalWithoutCost) after += Math.Abs(replacementAmount);
            replaced++;
            if (externalWithoutCost) voided++;
            affectedOrders.Add(row.Order.Id);
            if (!apply) continue;

            var ownsTransaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction == null;
            await using var transaction = ownsTransaction
                ? await _db.Database.BeginTransactionAsync(cancellationToken)
                : null;
            var payload = JsonSerializer.Serialize(new
            {
                repair = RepairVersion,
                originalEntryId = row.Entry.Id,
                originalAmountCents = row.Entry.AmountCents,
                replacementAmountCents = replacementAmount,
                row.Item.SabrVariantSku,
                row.Item.Quantity,
                source = externalWithoutCost ? "EXTERNAL_COST_PENDING" : resolved!.Origin,
                reason = externalWithoutCost
                    ? "Custo externo histórico sem snapshot confiável"
                    : "Reparo global de custo histórico por snapshot do catálogo do cliente"
            });
            var replacement = await _ledger.AppendAsync(new AppendFinancialEntryRequest
            {
                TenantId = row.Entry.TenantId,
                ClientId = row.Entry.ClientId,
                Provider = row.Entry.Provider,
                SellerId = row.Entry.SellerId,
                EntryType = row.Entry.EntryType,
                Layer = row.Entry.Layer,
                Status = externalWithoutCost ? FinancialEntryStatuses.Voided : row.Entry.Status,
                AmountCents = replacementAmount,
                CurrencyId = row.Entry.CurrencyId,
                EconomicKey = row.Entry.EconomicKey,
                IdempotencyKey = $"{RepairVersion}:{row.Entry.Id:N}",
                MarketplaceOrderId = row.Entry.MarketplaceOrderId,
                MarketplaceOrderItemId = row.Entry.MarketplaceOrderItemId,
                ExternalOrderId = row.Entry.ExternalOrderId,
                ExternalPaymentId = row.Entry.ExternalPaymentId,
                ExternalShipmentId = row.Entry.ExternalShipmentId,
                ExternalPackId = row.Entry.ExternalPackId,
                ExternalClaimId = row.Entry.ExternalClaimId,
                ExternalReturnId = row.Entry.ExternalReturnId,
                EconomicOccurredAt = row.Entry.EconomicOccurredAt,
                FinancialConfirmedAt = row.Entry.FinancialConfirmedAt,
                ProviderUpdatedAt = row.Entry.ProviderUpdatedAt,
                SourceEndpoint = "financial-repair/legacy-product-cost-v1",
                SourceRecordId = row.Entry.Id.ToString("N"),
                CanonicalPayloadHash = Hash(payload),
                MetadataJson = payload
            }, cancellationToken);

            var item = await _db.MarketplaceOrderItems.SingleAsync(x => x.Id == row.Item.Id, cancellationToken);
            item.ProductCostEntryId = replacement.Id;
            if (externalWithoutCost)
            {
                item.MappingState = MarketplaceMappingStates.ExternalCostPending;
                item.InternalCostStatus = InternalCostStatuses.Voided;
            }
            else
            {
                item.CatalogUnitPriceCentsAtPayment = resolved!.CatalogPriceCents;
                item.CostUnitPriceCentsAtPayment = resolved.CatalogPriceCents;
                item.ChargeLineTotalCentsAtPayment = checked(resolved.CatalogPriceCents * item.Quantity);
                item.EconomicAt = resolved.EconomicAt;
                item.EconomicAtSource = resolved.EconomicAtSource;
                item.CostSource = "CATALOG_PRICE";
                item.CatalogPriceVersionId = resolved.VersionId == Guid.Empty ? null : resolved.VersionId;
                item.CatalogCostBaselineId = resolved.BaselineId;
                item.CostReferencesJson = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        source = resolved.Origin,
                        quantity = item.Quantity,
                        unitCostCents = resolved.CatalogPriceCents,
                        catalogPriceVersionId = resolved.VersionId == Guid.Empty ? (Guid?)null : resolved.VersionId,
                        catalogCostBaselineId = resolved.BaselineId
                    }
                });
                item.InternalCostStatus = replacement.Status == FinancialEntryStatuses.Confirmed
                    ? InternalCostStatuses.Settled
                    : InternalCostStatuses.Accrued;
            }
            await _db.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
        }

        if (apply)
        {
            foreach (var orderId in affectedOrders.Order())
            {
                var order = await _db.MarketplaceOrders.Include(x => x.Items)
                    .SingleAsync(x => x.Id == orderId, cancellationToken);
                await _projection.RebuildOrderStateAsync(order, cancellationToken);
            }
        }

        return new LegacyFinancialCostRepairResult(accounts.Count, replaced, voided,
            affectedOrders.Count, before, after, before - after);
    }

    private static string Hash(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    private sealed record Candidate(Guid HeadId, MarketplaceFinancialEntry Entry,
        MarketplaceOrderItem Item, MarketplaceOrder Order);
}
