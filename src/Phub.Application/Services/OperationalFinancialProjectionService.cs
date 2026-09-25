using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Entities;

namespace Phub.Application.Services;

/// <summary>
/// Produces the fast operational layer from order facts. Billing confirmation is
/// intentionally handled by a separate reconciler.
/// </summary>
public sealed class OperationalFinancialProjectionService
{
    private static readonly HashSet<string> CancelledSaleComponents = new(StringComparer.Ordinal)
    {
        FinancialEntryTypes.GrossSale,
        FinancialEntryTypes.BuyerDiscount,
        FinancialEntryTypes.SaleFee,
        FinancialEntryTypes.FinancingOrFixedFee,
        FinancialEntryTypes.Refund,
        FinancialEntryTypes.ProductCost
    };

    private readonly IAppDbContext _db;
    private readonly FinancialLedgerService _ledger;
    private readonly HistoricalProductCostService _historicalCosts;

    public OperationalFinancialProjectionService(IAppDbContext db, FinancialLedgerService ledger)
        : this(db, ledger, new HistoricalProductCostService(db))
    {
    }

    public OperationalFinancialProjectionService(IAppDbContext db, FinancialLedgerService ledger,
        HistoricalProductCostService historicalCosts)
    {
        _db = db;
        _ledger = ledger;
        _historicalCosts = historicalCosts;
    }

    public async Task ProjectOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await _db.MarketplaceOrders.Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == orderId, cancellationToken);
        if (order == null) return;
        var refunds = ReadRefunds(order.RawJson);
        if (IsCancelledOrder(order.Status))
        {
            await VoidCancelledSaleAsync(order, cancellationToken);
            await RebuildOrderStateAsync(order, cancellationToken);
            return;
        }
        if (!IsFinancialOrder(order.Status) && refunds.Count == 0) return;

        var occurredAt = order.PaidAt ?? order.ChannelCreatedAt ?? order.ImportedAt;
        var currency = string.IsNullOrWhiteSpace(order.CurrencyId) ? "BRL" : order.CurrencyId!;
        foreach (var item in order.Items)
        {
            var line = item.Id.ToString("N");
            var itemRevenue = item.GrossPrice ?? ((item.UnitPrice ?? item.FullUnitPrice ?? 0m) * item.Quantity);
            await AppendIfNonZeroAsync(order, item, FinancialEntryTypes.GrossSale,
                ToCents(itemRevenue), $"ML:{order.SellerId}:ORDER:{order.MlOrderId}:ITEM:{line}:GROSS_SALE",
                occurredAt, currency, item.RawJson ?? "{}", cancellationToken);

            await AppendIfNonZeroAsync(order, item, FinancialEntryTypes.SaleFee,
                -Math.Abs(ToCents(item.SaleFee ?? 0m)), $"ML:{order.SellerId}:ORDER:{order.MlOrderId}:ITEM:{line}:SALE_FEE",
                occurredAt, currency, item.RawJson ?? "{}", cancellationToken);

            if (item.MappingState == MarketplaceMappingStates.ExternalSupplier && item.ExternalUnitCostCentsSnapshot.HasValue)
            {
                var productCostKey = $"PHUB:{order.ClientId}:ORDER:{order.MlOrderId}:ITEM:{line}:PRODUCT_COST";
                var snapshot = JsonSerializer.Serialize(new
                {
                    item.ExternalSupplierName,
                    externalUnitCostCents = item.ExternalUnitCostCentsSnapshot.Value,
                    externalCostVersionId = item.ExternalCostVersionId,
                    item.Quantity,
                    source = "EXTERNAL_SUPPLIER"
                });
                var costEntry = await AppendIfNonZeroAsync(order, item, FinancialEntryTypes.ProductCost,
                    -checked(item.ExternalUnitCostCentsSnapshot.Value * item.Quantity),
                    productCostKey, occurredAt, currency, snapshot, cancellationToken);
                MarkAccrued(item, costEntry, occurredAt);
            }
            else if (!string.IsNullOrWhiteSpace(item.SabrVariantSku))
            {
                var productCostKey = $"PHUB:{order.ClientId}:ORDER:{order.MlOrderId}:ITEM:{line}:PRODUCT_COST";
                var currentCost = await (from head in _db.FinancialEconomicHeads.AsNoTracking()
                    join entry in _db.MarketplaceFinancialEntries.AsNoTracking() on head.ActiveEntryId equals entry.Id
                    where head.TenantId == order.TenantId && head.ClientId == order.ClientId
                          && head.Provider == order.Provider && head.SellerId == order.SellerId
                          && head.EconomicKey == productCostKey
                    select entry).FirstOrDefaultAsync(cancellationToken);
                // The paid item snapshot is authoritative. A current catalog price is used
                // only for the first estimate; the marketplace sale price is never a cost.
                if (currentCost != null && !item.CatalogUnitPriceCentsAtPayment.HasValue) continue;
                var historicalCost = item.CatalogUnitPriceCentsAtPayment.HasValue
                    ? null
                    : await _historicalCosts.ResolveAsync(order, item, cancellationToken);
                var catalogPrice = item.CatalogUnitPriceCentsAtPayment ?? historicalCost?.CatalogPriceCents;
                if (catalogPrice.HasValue && catalogPrice.Value > 0)
                {
                    if (!item.CatalogUnitPriceCentsAtPayment.HasValue && historicalCost != null)
                    {
                        item.CatalogUnitPriceCentsAtPayment = historicalCost.CatalogPriceCents;
                        item.CostUnitPriceCentsAtPayment = historicalCost.CatalogPriceCents;
                        item.ChargeLineTotalCentsAtPayment = checked(historicalCost.CatalogPriceCents * item.Quantity);
                        item.EconomicAt = historicalCost.EconomicAt;
                        item.EconomicAtSource = historicalCost.EconomicAtSource;
                        item.CostSource = "CATALOG_PRICE";
                        item.CatalogPriceVersionId = historicalCost.VersionId;
                        item.CostReferencesJson = JsonSerializer.Serialize(new[]
                        {
                            new
                            {
                                source = "GENERAL_STOCK",
                                lotId = (Guid?)null,
                                quantity = item.Quantity,
                                unitCostCents = historicalCost.CatalogPriceCents,
                                catalogPriceVersionId = historicalCost.VersionId,
                                priceOrigin = historicalCost.Origin
                            }
                        });
                    }
                    var expectedCost = -checked(catalogPrice.Value * item.Quantity);
                    if (currentCost != null && currentCost.Status != FinancialEntryStatuses.Voided
                        && (currentCost.AmountCents == expectedCost
                        || currentCost.Status == FinancialEntryStatuses.Confirmed)) continue;
                    var snapshot = JsonSerializer.Serialize(new
                    {
                        item.SabrVariantSku,
                        catalogUnitPriceCents = catalogPrice.Value,
                        item.Quantity,
                        source = "HISTORICAL_INTERNAL_CATALOG",
                        item.CatalogPriceVersionId,
                        item.EconomicAt,
                        item.EconomicAtSource
                    });
                    var costEntry = await AppendIfNonZeroAsync(order, item, FinancialEntryTypes.ProductCost,
                        expectedCost,
                        productCostKey,
                        occurredAt, currency, snapshot, cancellationToken);
                    MarkAccrued(item, costEntry ?? currentCost, occurredAt);
                }
            }
        }

        foreach (var refund in refunds)
        {
            await AppendFactAsync(order, null, FinancialEntryTypes.Refund, -Math.Abs(refund.AmountCents),
                $"ML:{order.SellerId}:PAYMENT:{refund.PaymentId}:REFUND:{refund.RefundId}",
                $"PAYMENT:{refund.PaymentId}:REFUND:{refund.RefundId}:{Hash(refund.RawJson)}",
                refund.OccurredAt ?? occurredAt, currency, $"/orders/{order.MlOrderId}", refund.RefundId,
                refund.RawJson, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await RebuildOrderStateAsync(order, cancellationToken);
    }

    public async Task ProjectExternalFactsAsync(Guid orderId, MercadoLivreShipmentCostDetails? shipping,
        IReadOnlyList<MercadoLivreOrderDiscountDetails> discounts, CancellationToken cancellationToken = default)
    {
        var order = await _db.MarketplaceOrders.Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == orderId, cancellationToken);
        if (order == null || (!IsFinancialOrder(order.Status) && !IsCancelledOrder(order.Status))) return;
        var occurredAt = order.PaidAt ?? order.ChannelCreatedAt ?? order.ImportedAt;
        var currency = string.IsNullOrWhiteSpace(order.CurrencyId) ? "BRL" : order.CurrencyId!;

        if (shipping is { SellerCost: > 0 })
        {
            await AppendFactAsync(order, null, FinancialEntryTypes.SellerShippingCost, -Math.Abs(ToCents(shipping.SellerCost)),
                $"ML:{order.SellerId}:SHIPMENT:{shipping.ShipmentId}:SHIPPING_COST",
                $"SHIPMENT:{shipping.ShipmentId}:SHIPPING_COST:{Hash(shipping.RawJson)}", occurredAt, shipping.CurrencyId,
                $"/shipments/{shipping.ShipmentId}/costs", shipping.ShipmentId, shipping.RawJson, cancellationToken);
        }
        if (shipping is { SellerCompensation: > 0 })
        {
            await AppendFactAsync(order, null, FinancialEntryTypes.ShippingDiscountOrCompensation, ToCents(shipping.SellerCompensation),
                $"ML:{order.SellerId}:SHIPMENT:{shipping.ShipmentId}:SHIPPING_BENEFIT:COMPENSATION",
                $"SHIPMENT:{shipping.ShipmentId}:COMPENSATION:{Hash(shipping.RawJson)}", occurredAt, shipping.CurrencyId,
                $"/shipments/{shipping.ShipmentId}/costs", shipping.ShipmentId, shipping.RawJson, cancellationToken);
        }
        // A cancelled sale is excluded from revenue and item-level commercial
        // discounts. Real shipment charges/credits remain, so a cancellation
        // can still have a negative (or compensating) economic result.
        foreach (var discount in discounts.Where(x => !IsCancelledOrder(order.Status) && x.SellerAmount != 0))
        {
            var candidates = order.Items.Where(x => string.Equals(x.MlItemId, discount.ItemId, StringComparison.Ordinal)).ToList();
            var item = candidates.Count == 1 ? candidates[0] : null; // Never invent allocation when the source is ambiguous.
            await AppendFactAsync(order, item, FinancialEntryTypes.BuyerDiscount, -Math.Abs(ToCents(discount.SellerAmount)),
                $"ML:{order.SellerId}:ORDER:{order.MlOrderId}:DISCOUNT:{discount.DiscountId}",
                $"ORDER:{order.MlOrderId}:DISCOUNT:{discount.DiscountId}:{Hash(discount.RawJson)}", occurredAt, currency,
                $"/orders/{order.MlOrderId}/discounts", discount.DiscountId, discount.RawJson, cancellationToken);
        }
        await RebuildOrderStateAsync(order, cancellationToken);
    }

    public async Task ConfirmProductCostsAsync(Guid orderId, DateTimeOffset confirmedAt, CancellationToken cancellationToken = default)
    {
        var order = await _db.MarketplaceOrders.Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == orderId, cancellationToken);
        if (order == null) return;
        var itemById = order.Items.ToDictionary(x => x.Id);
        var costs = await (from head in _db.FinancialEconomicHeads.AsNoTracking()
                           join entry in _db.MarketplaceFinancialEntries.AsNoTracking() on head.ActiveEntryId equals entry.Id
                           where entry.MarketplaceOrderId == orderId && entry.EntryType == FinancialEntryTypes.ProductCost
                           select entry).ToListAsync(cancellationToken);
        foreach (var current in costs.Where(x => x.Status is not FinancialEntryStatuses.Confirmed
                                                  and not FinancialEntryStatuses.Voided))
        {
            itemById.TryGetValue(current.MarketplaceOrderItemId ?? Guid.Empty, out var item);
            var payload = JsonSerializer.Serialize(new { confirmedBy = "PROMETHEUSHUB_WALLET", confirmedAt, sourceEntryId = current.Id });
            var confirmed = await _ledger.AppendAsync(new AppendFinancialEntryRequest
            {
                TenantId = current.TenantId, ClientId = current.ClientId, Provider = current.Provider, SellerId = current.SellerId,
                EntryType = current.EntryType, Layer = FinancialLayers.InternalConfirmed, Status = FinancialEntryStatuses.Confirmed,
                AmountCents = current.AmountCents, CurrencyId = current.CurrencyId, EconomicKey = current.EconomicKey,
                IdempotencyKey = $"WALLET:ORDER:{order.MlOrderId}:ITEM:{item?.Id:N}:PRODUCT_COST:CONFIRMED",
                MarketplaceOrderId = order.Id, MarketplaceOrderItemId = item?.Id, ExternalOrderId = order.MlOrderId,
                EconomicOccurredAt = current.EconomicOccurredAt, FinancialConfirmedAt = confirmedAt,
                SourceEndpoint = "/api/v1/client/orders/{orderId}/pay", SourceRecordId = order.Id.ToString("N"),
                CanonicalPayloadHash = Hash(payload), MetadataJson = payload
            }, cancellationToken);
            if (item != null)
            {
                item.InternalCostStatus = InternalCostStatuses.Settled;
                item.ProductCostEntryId = confirmed.Id;
                item.InternalWalletEntryId = order.WalletLedgerEntryId;
                item.CostAccruedAt ??= current.EconomicOccurredAt;
                item.CostSettledAt = confirmedAt;
            }
        }
        await _db.SaveChangesAsync(cancellationToken);
        await RebuildOrderStateAsync(order, cancellationToken);
    }

    private async Task VoidCancelledSaleAsync(MarketplaceOrder order, CancellationToken cancellationToken)
    {
        var active = await (from head in _db.FinancialEconomicHeads.AsNoTracking()
                            join entry in _db.MarketplaceFinancialEntries.AsNoTracking() on head.ActiveEntryId equals entry.Id
                            where head.TenantId == order.TenantId && head.ClientId == order.ClientId
                                  && head.Provider == order.Provider && head.SellerId == order.SellerId
                                  && entry.MarketplaceOrderId == order.Id
                                  && CancelledSaleComponents.Contains(entry.EntryType)
                                  && entry.Status != FinancialEntryStatuses.Voided
                            select entry).ToListAsync(cancellationToken);

        foreach (var current in active)
        {
            // A settled internal obligation remains an historical fact. A
            // later cancellation is handled by refund/return/recovery facts.
            if (current.EntryType == FinancialEntryTypes.ProductCost
                && (current.Status == FinancialEntryStatuses.Confirmed
                    || order.Items.Any(x => x.Id == current.MarketplaceOrderItemId
                        && x.InternalCostStatus == InternalCostStatuses.Settled)))
                continue;
            var payload = JsonSerializer.Serialize(new
            {
                correction = "ORDER_CANCELLED",
                orderStatus = order.Status,
                voidedEntryId = current.Id,
                preservedAmountCents = current.AmountCents,
                providerUpdatedAt = order.UpdatedAt
            });
            var voided = await _ledger.AppendAsync(new AppendFinancialEntryRequest
            {
                TenantId = current.TenantId,
                ClientId = current.ClientId,
                Provider = current.Provider,
                SellerId = current.SellerId,
                EntryType = current.EntryType,
                Layer = current.Layer,
                Status = FinancialEntryStatuses.Voided,
                // The amount is retained for audit. VOIDED heads are excluded
                // from every aggregate and therefore have economic value zero.
                AmountCents = current.AmountCents,
                CurrencyId = current.CurrencyId,
                EconomicKey = current.EconomicKey,
                IdempotencyKey = $"ORDER:{order.MlOrderId}:CANCEL:VOID:{current.Id:N}",
                MarketplaceOrderId = current.MarketplaceOrderId,
                MarketplaceOrderItemId = current.MarketplaceOrderItemId,
                ExternalOrderId = current.ExternalOrderId,
                ExternalPaymentId = current.ExternalPaymentId,
                ExternalShipmentId = current.ExternalShipmentId,
                ExternalPackId = current.ExternalPackId,
                ExternalClaimId = current.ExternalClaimId,
                ExternalReturnId = current.ExternalReturnId,
                EconomicOccurredAt = current.EconomicOccurredAt,
                ProviderUpdatedAt = order.UpdatedAt,
                SourceEndpoint = $"/orders/{order.MlOrderId}",
                SourceRecordId = order.MlOrderId,
                CanonicalPayloadHash = Hash(payload),
                MetadataJson = payload
            }, cancellationToken);
            var item = order.Items.FirstOrDefault(x => x.Id == current.MarketplaceOrderItemId);
            if (item != null && current.EntryType == FinancialEntryTypes.ProductCost)
            {
                item.InternalCostStatus = InternalCostStatuses.Voided;
                item.ProductCostEntryId = voided.Id;
            }
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task RebuildOrderStateAsync(MarketplaceOrder order, CancellationToken cancellationToken = default)
    {
        var activeEntries = await (
            from head in _db.FinancialEconomicHeads.AsNoTracking()
            join entry in _db.MarketplaceFinancialEntries.AsNoTracking() on head.ActiveEntryId equals entry.Id
            where head.TenantId == order.TenantId && head.ClientId == order.ClientId && head.Provider == order.Provider
                  && head.SellerId == order.SellerId && entry.MarketplaceOrderId == order.Id
            select entry).ToListAsync(cancellationToken);
        activeEntries = activeEntries.Where(x => x.Status != FinancialEntryStatuses.Voided).ToList();

        var itemIdsWithCost = activeEntries.Where(x => x.EntryType == FinancialEntryTypes.ProductCost && x.MarketplaceOrderItemId.HasValue)
            .Select(x => x.MarketplaceOrderItemId!.Value).ToHashSet();
        var skuResolved = order.Items.Count > 0 && order.Items.All(x => !string.IsNullOrWhiteSpace(x.SabrVariantSku)
            || MarketplaceMappingStates.IsExternal(x.MappingState));
        var costResolved = order.Items.Count > 0 && order.Items.All(x => itemIdsWithCost.Contains(x.Id));
        var grossResolved = order.Items.Count > 0 && order.Items.All(x => x.GrossPrice.HasValue || x.UnitPrice.HasValue || x.FullUnitPrice.HasValue);
        var feeResolved = order.Items.Count > 0 && order.Items.All(x => x.SaleFee.HasValue);
        var freightResolved = string.IsNullOrWhiteSpace(order.ShipmentId)
                              || activeEntries.Any(x => x.EntryType is FinancialEntryTypes.SellerShippingCost
                                  or FinancialEntryTypes.ShippingDiscountOrCompensation);
        var allocationResolved = !activeEntries.Any(x => x.MarketplaceOrderItemId == null
            && x.EntryType is FinancialEntryTypes.Refund or FinancialEntryTypes.ChargebackOrClaim or FinancialEntryTypes.PlatformAdjustment);
        var operationalResolved = skuResolved && costResolved && grossResolved && feeResolved && freightResolved && allocationResolved;
        var externalEntries = activeEntries.Where(x => x.Layer != FinancialLayers.InternalConfirmed).ToList();
        var anyConfirmed = externalEntries.Any(x => x.Status == FinancialEntryStatuses.Confirmed);
        var confirmedResolved = externalEntries.Count > 0 && externalEntries.All(x => x.Status == FinancialEntryStatuses.Confirmed);

        var reasons = new List<string>();
        if (!skuResolved) reasons.Add("SKU_PENDING");
        if (order.Items.Any(x => x.MappingState == MarketplaceMappingStates.ExternalCostPending)) reasons.Add("EXTERNAL_COST_PENDING");
        if (order.Items.Any(x => !MarketplaceMappingStates.IsExternal(x.MappingState)
                                 && !itemIdsWithCost.Contains(x.Id))) reasons.Add("CATALOG_COST_PENDING");
        if (!grossResolved) reasons.Add("GROSS_REVENUE_PENDING");
        if (!feeResolved) reasons.Add("MARKETPLACE_FEE_PENDING");
        if (!freightResolved) reasons.Add("SHIPPING_COST_PENDING");
        if (!allocationResolved) reasons.Add("UNALLOCATED_EXTERNAL_VALUE");

        var state = await _db.MarketplaceOrderFinancialStates.FirstOrDefaultAsync(x => x.MarketplaceOrderId == order.Id, cancellationToken);
        var isNewState = state == null;
        var wasConfirmed = state?.WasConfirmedAt;
        var maturity = !operationalResolved ? FinancialMaturity.Incomplete
            : confirmedResolved ? FinancialMaturity.Confirmed
            : anyConfirmed ? FinancialMaturity.PartiallyConfirmed
            : FinancialMaturity.Estimated;
        if (wasConfirmed.HasValue && maturity != FinancialMaturity.Confirmed) maturity = FinancialMaturity.Reopened;

        var gross = Sum(activeEntries, FinancialEntryTypes.GrossSale);
        var externalNet = activeEntries.Where(x => x.EntryType != FinancialEntryTypes.ProductCost
                                                    && x.EntryType != FinancialEntryTypes.ProductCostRecovery
                                                    && x.EntryType != FinancialEntryTypes.SellerTaxEstimate)
            .Sum(x => x.AmountCents);
        var productCost = Sum(activeEntries, FinancialEntryTypes.ProductCost, FinancialEntryTypes.ProductCostRecovery);
        var confirmed = activeEntries.Where(x => x.Layer == FinancialLayers.Reconciled
                                                  && x.Status == FinancialEntryStatuses.Confirmed)
            .Sum(x => x.AmountCents);
        var unallocated = activeEntries.Where(x => x.MarketplaceOrderItemId == null
            && x.EntryType is FinancialEntryTypes.Refund or FinancialEntryTypes.ChargebackOrClaim or FinancialEntryTypes.PlatformAdjustment)
            .Sum(x => x.AmountCents);

        state ??= new MarketplaceOrderFinancialState
        {
            MarketplaceOrderId = order.Id,
            TenantId = order.TenantId,
            ClientId = order.ClientId,
            Provider = order.Provider,
            SellerId = order.SellerId
        };
        if (isNewState)
            _db.MarketplaceOrderFinancialStates.Add(state);
        state.Maturity = maturity;
        state.GrossRevenueCents = gross;
        state.EstimatedEconomicNetCents = externalNet;
        state.ConfirmedValueCents = confirmed;
        state.OperationalProfitCents = externalNet + productCost;
        state.UnallocatedCents = unallocated;
        state.SkuResolved = skuResolved;
        state.CostResolved = costResolved;
        state.FreightResolved = freightResolved;
        state.OperationalComponentsResolved = operationalResolved;
        state.ConfirmedComponentsResolved = confirmedResolved;
        state.ItemAllocationResolved = allocationResolved;
        state.IncompleteReasonsJson = JsonSerializer.Serialize(reasons);
        state.DivergenceJson = BuildDivergenceJson(activeEntries);
        state.WasConfirmedAt = maturity == FinancialMaturity.Confirmed ? wasConfirmed ?? DateTimeOffset.UtcNow : wasConfirmed;
        state.ReopenedAt = maturity == FinancialMaturity.Reopened ? state.ReopenedAt ?? DateTimeOffset.UtcNow : state.ReopenedAt;
        state.LastProjectedAt = DateTimeOffset.UtcNow;
        state.Version = isNewState ? 1 : state.Version + 1;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<MarketplaceFinancialEntry?> AppendIfNonZeroAsync(MarketplaceOrder order, MarketplaceOrderItem item, string type, long amount,
        string economicKey, DateTimeOffset occurredAt, string currency, string payload, CancellationToken cancellationToken)
    {
        if (amount == 0) return null;
        var hash = Hash(payload);
        var activeStatus = await (from head in _db.FinancialEconomicHeads.AsNoTracking()
                                  join entry in _db.MarketplaceFinancialEntries.AsNoTracking() on head.ActiveEntryId equals entry.Id
                                  where head.TenantId == order.TenantId && head.ClientId == order.ClientId
                                        && head.Provider == order.Provider && head.SellerId == order.SellerId
                                        && head.EconomicKey == economicKey
                                  select new { entry.Id, entry.Status }).FirstOrDefaultAsync(cancellationToken);
        var idempotencyKey = $"ORDER:{order.MlOrderId}:{type}:{item.Id:N}:{hash}";
        if (activeStatus?.Status == FinancialEntryStatuses.Voided)
            idempotencyKey += $":REACTIVATE:{activeStatus.Id:N}";
        return await _ledger.AppendAsync(new AppendFinancialEntryRequest
        {
            TenantId = order.TenantId,
            ClientId = order.ClientId,
            Provider = order.Provider,
            SellerId = order.SellerId,
            EntryType = type,
            Layer = FinancialLayers.Operational,
            Status = FinancialEntryStatuses.Estimated,
            AmountCents = amount,
            CurrencyId = currency,
            EconomicKey = economicKey,
            IdempotencyKey = idempotencyKey,
            MarketplaceOrderId = order.Id,
            MarketplaceOrderItemId = item.Id,
            ExternalOrderId = order.MlOrderId,
            ExternalShipmentId = order.ShipmentId,
            EconomicOccurredAt = occurredAt,
            ProviderUpdatedAt = order.UpdatedAt,
            SourceEndpoint = $"/orders/{order.MlOrderId}",
            SourceRecordId = item.Id.ToString("N"),
            CanonicalPayloadHash = hash,
            MetadataJson = payload
        }, cancellationToken);
    }

    private static void MarkAccrued(MarketplaceOrderItem item, MarketplaceFinancialEntry? entry, DateTimeOffset occurredAt)
    {
        if (entry == null || entry.Status == FinancialEntryStatuses.Voided) return;
        item.InternalCostStatus = entry.Status == FinancialEntryStatuses.Confirmed
            ? InternalCostStatuses.Settled
            : InternalCostStatuses.Accrued;
        item.ProductCostEntryId = entry.Id;
        item.CostAccruedAt ??= occurredAt;
        if (entry.Status == FinancialEntryStatuses.Confirmed)
            item.CostSettledAt ??= entry.FinancialConfirmedAt;
    }

    private async Task AppendFactAsync(MarketplaceOrder order, MarketplaceOrderItem? item, string type, long amount,
        string economicKey, string idempotencyKey, DateTimeOffset occurredAt, string currency, string endpoint,
        string recordId, string payload, CancellationToken cancellationToken)
    {
        if (amount == 0) return;
        await _ledger.AppendAsync(new AppendFinancialEntryRequest
        {
            TenantId = order.TenantId, ClientId = order.ClientId, Provider = order.Provider, SellerId = order.SellerId,
            EntryType = type, Layer = FinancialLayers.Operational, Status = FinancialEntryStatuses.Estimated,
            AmountCents = amount, CurrencyId = currency, EconomicKey = economicKey, IdempotencyKey = idempotencyKey,
            MarketplaceOrderId = order.Id, MarketplaceOrderItemId = item?.Id, ExternalOrderId = order.MlOrderId,
            ExternalShipmentId = order.ShipmentId, EconomicOccurredAt = occurredAt, ProviderUpdatedAt = order.UpdatedAt,
            SourceEndpoint = endpoint, SourceRecordId = recordId, CanonicalPayloadHash = Hash(payload), MetadataJson = payload
        }, cancellationToken);
    }

    private static bool IsFinancialOrder(string? status) => string.Equals(status, "paid", StringComparison.OrdinalIgnoreCase)
                                                            || string.Equals(status, "partially_refunded", StringComparison.OrdinalIgnoreCase)
                                                            || string.Equals(status, "refunded", StringComparison.OrdinalIgnoreCase);
    private static bool IsCancelledOrder(string? status) => status?.Trim().ToLowerInvariant() is "cancelled" or "canceled";
    private static long ToCents(decimal value) => checked((long)Math.Round(value * 100m, MidpointRounding.AwayFromZero));
    private static long Sum(IEnumerable<MarketplaceFinancialEntry> entries, params string[] types)
        => entries.Where(x => types.Contains(x.EntryType, StringComparer.Ordinal)).Sum(x => x.AmountCents);
    private static string Hash(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    private static List<RefundFact> ReadRefunds(string? rawJson)
    {
        var result = new List<RefundFact>();
        if (string.IsNullOrWhiteSpace(rawJson)) return result;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (!doc.RootElement.TryGetProperty("payments", out var payments) || payments.ValueKind != JsonValueKind.Array) return result;
            foreach (var payment in payments.EnumerateArray())
            {
                var paymentId = ReadString(payment, "id");
                if (string.IsNullOrWhiteSpace(paymentId)) continue;
                if (payment.TryGetProperty("refunds", out var refunds) && refunds.ValueKind == JsonValueKind.Array)
                {
                    foreach (var refund in refunds.EnumerateArray())
                    {
                        var amount = ReadDecimal(refund, "amount");
                        if (amount <= 0) continue;
                        var refundId = ReadString(refund, "id") ?? Hash(refund.GetRawText())[..16];
                        result.Add(new RefundFact(paymentId, refundId, ToCents(amount), ReadDate(refund, "date_created"), refund.GetRawText()));
                    }
                    continue;
                }
                var aggregate = ReadDecimal(payment, "transaction_amount_refunded");
                if (aggregate > 0)
                    result.Add(new RefundFact(paymentId, "aggregate", ToCents(aggregate), ReadDate(payment, "date_last_modified"), payment.GetRawText()));
            }
        }
        catch (JsonException) { }
        return result;
    }

    private static string? ReadString(JsonElement node, string property) => !node.TryGetProperty(property, out var value) ? null
        : value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
    private static decimal ReadDecimal(JsonElement node, string property) => node.TryGetProperty(property, out var value)
        && (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            || value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out number)) ? number : 0m;
    private static DateTimeOffset? ReadDate(JsonElement node, string property) => DateTimeOffset.TryParse(ReadString(node, property), out var value) ? value.ToUniversalTime() : null;
    private sealed record RefundFact(string PaymentId, string RefundId, long AmountCents, DateTimeOffset? OccurredAt, string RawJson);

    private static string BuildDivergenceJson(IEnumerable<MarketplaceFinancialEntry> entries)
    {
        var rows = entries.ToList();
        var groups = rows.GroupBy(x => x.EntryType).ToDictionary(
            x => x.Key,
            x => x.Where(e => e.Status == FinancialEntryStatuses.Confirmed).Sum(e => e.AmountCents)
                 - x.Where(e => e.Status == FinancialEntryStatuses.Estimated).Sum(e => e.AmountCents));
        return JsonSerializer.Serialize(groups);
    }
}
