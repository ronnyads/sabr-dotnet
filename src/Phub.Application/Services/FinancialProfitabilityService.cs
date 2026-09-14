using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class FinancialProfitabilityService
{
    private readonly IAppDbContext _db;

    public FinancialProfitabilityService(IAppDbContext db) => _db = db;

    public async Task<ClientProfitabilityResult> GetAsync(
        string tenantId,
        Guid clientId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        MarketplaceProvider? provider,
        long? sellerId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rangeTo = (to ?? now).ToUniversalTime();
        var rangeFrom = (from ?? rangeTo.AddDays(-29)).ToUniversalTime();
        if (rangeFrom > rangeTo) (rangeFrom, rangeTo) = (rangeTo, rangeFrom);
        if (rangeTo - rangeFrom > TimeSpan.FromDays(366)) rangeFrom = rangeTo.AddDays(-366);

        var orderQuery = _db.MarketplaceOrders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientId == clientId)
            .Where(x => (x.PaidAt ?? x.ChannelCreatedAt ?? x.ImportedAt) >= rangeFrom
                        && (x.PaidAt ?? x.ChannelCreatedAt ?? x.ImportedAt) <= rangeTo);
        if (provider.HasValue) orderQuery = orderQuery.Where(x => x.Provider == provider.Value);
        if (sellerId.HasValue) orderQuery = orderQuery.Where(x => x.SellerId == sellerId.Value);
        var orderIds = await orderQuery.Select(x => x.Id).ToListAsync(cancellationToken);

        var statesQuery = _db.MarketplaceOrderFinancialStates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientId == clientId && orderIds.Contains(x.MarketplaceOrderId));
        if (provider.HasValue) statesQuery = statesQuery.Where(x => x.Provider == provider.Value);
        if (sellerId.HasValue) statesQuery = statesQuery.Where(x => x.SellerId == sellerId.Value);
        var states = await statesQuery.ToListAsync(cancellationToken);

        var entriesQuery = _db.MarketplaceFinancialEntries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientId == clientId
                        && x.MarketplaceOrderId.HasValue && orderIds.Contains(x.MarketplaceOrderId.Value));
        if (provider.HasValue) entriesQuery = entriesQuery.Where(x => x.Provider == provider.Value);
        if (sellerId.HasValue) entriesQuery = entriesQuery.Where(x => x.SellerId == sellerId.Value);
        var allEntries = await entriesQuery.ToListAsync(cancellationToken);

        var activeIds = await _db.FinancialEconomicHeads.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientId == clientId)
            .Where(x => !provider.HasValue || x.Provider == provider.Value)
            .Where(x => !sellerId.HasValue || x.SellerId == sellerId.Value)
            .Select(x => x.ActiveEntryId)
            .ToListAsync(cancellationToken);
        var activeSet = activeIds.ToHashSet();
        var activeEntries = allEntries.Where(x => activeSet.Contains(x.Id)).ToList();

        var estimatedByKey = allEntries.Where(x => x.Status == FinancialEntryStatuses.Estimated)
            .GroupBy(x => x.EconomicKey).ToDictionary(x => x.Key, x => x.OrderByDescending(e => e.ObservedAt).First());
        var confirmedByKey = allEntries.Where(x => x.Status == FinancialEntryStatuses.Confirmed)
            .GroupBy(x => x.EconomicKey).ToDictionary(x => x.Key, x => x.OrderByDescending(e => e.FinancialConfirmedAt).First());
        var componentDeltas = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var key in estimatedByKey.Keys.Union(confirmedByKey.Keys))
        {
            estimatedByKey.TryGetValue(key, out var estimated);
            confirmedByKey.TryGetValue(key, out var confirmed);
            if (estimated == null || confirmed == null) continue;
            var component = ToComponent(confirmed.EntryType);
            componentDeltas[component] = componentDeltas.GetValueOrDefault(component) + confirmed.AmountCents - estimated.AmountCents;
        }

        var estimatedTotal = estimatedByKey.Values.Sum(x => x.AmountCents);
        var confirmedTotal = confirmedByKey.Values.Sum(x => x.AmountCents);
        var delta = confirmedTotal - estimatedTotal;
        var count = states.Count;
        var taxRate = await ResolveTaxRateAsync(tenantId, clientId, sellerId, rangeTo, cancellationToken);
        var gross = activeEntries.Where(x => x.EntryType == FinancialEntryTypes.GrossSale).Sum(x => x.AmountCents);
        var externalNet = activeEntries.Where(x => x.EntryType is not FinancialEntryTypes.ProductCost
                                                   and not FinancialEntryTypes.ProductCostRecovery
                                                   and not FinancialEntryTypes.SellerTaxEstimate).Sum(x => x.AmountCents);
        var productCost = activeEntries.Where(x => x.EntryType is FinancialEntryTypes.ProductCost or FinancialEntryTypes.ProductCostRecovery)
            .Sum(x => x.AmountCents);
        var profit = externalNet + productCost;
        var tax = taxRate <= 0 ? 0L : -checked((long)Math.Round(gross * taxRate / 10_000m, MidpointRounding.AwayFromZero));

        var lastOperational = await _db.TenantMarketplaceConnections.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientId == clientId)
            .Where(x => !provider.HasValue || x.Provider == provider.Value)
            .Where(x => !sellerId.HasValue || x.SellerId == sellerId.Value)
            .MaxAsync(x => (DateTimeOffset?)x.LastSyncAt, cancellationToken);
        var lastBilling = allEntries.Where(x => x.Layer == FinancialLayers.Reconciled)
            .MaxBy(x => x.ObservedAt)?.ObservedAt;

        return new ClientProfitabilityResult
        {
            From = rangeFrom,
            To = rangeTo,
            GeneratedAt = now,
            LastOperationalSyncAt = lastOperational,
            LastBillingSyncAt = lastBilling,
            CurrencyId = activeEntries.Select(x => x.CurrencyId).FirstOrDefault() ?? "BRL",
            Maturity = AggregateMaturity(states),
            GrossRevenueCents = gross,
            EstimatedEconomicNetCents = externalNet,
            ReconciledConfirmedValueCents = activeEntries.Where(x => x.Status == FinancialEntryStatuses.Confirmed).Sum(x => x.AmountCents),
            OperationalProfitCents = profit,
            SellerReportedEstimatedTaxCents = tax,
            ProfitAfterSellerTaxEstimateCents = profit + tax,
            UnallocatedCents = states.Sum(x => x.UnallocatedCents),
            Coverage = BuildCoverage(states),
            Divergence = new FinancialDivergenceResult
            {
                EstimatedCents = estimatedTotal,
                ConfirmedCents = confirmedTotal,
                AbsoluteCents = delta,
                Percentage = estimatedTotal == 0 ? null : Math.Round(delta * 100m / Math.Abs(estimatedTotal), 2),
                ComponentsCents = componentDeltas
            },
            IncompleteReasons = states.SelectMany(ReadReasons).Distinct(StringComparer.Ordinal).OrderBy(x => x).ToList()
        };
    }

    public async Task<IReadOnlyList<ClientProfitabilityOrderResult>> GetOrdersAsync(
        string tenantId, Guid clientId, DateTimeOffset? from, DateTimeOffset? to,
        long? sellerId, CancellationToken cancellationToken)
    {
        var rangeTo = (to ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var rangeFrom = (from ?? rangeTo.AddDays(-29)).ToUniversalTime();
        if (rangeFrom > rangeTo) (rangeFrom, rangeTo) = (rangeTo, rangeFrom);

        var query = from order in _db.MarketplaceOrders.AsNoTracking()
                    join state in _db.MarketplaceOrderFinancialStates.AsNoTracking()
                        on order.Id equals state.MarketplaceOrderId
                    where order.TenantId == tenantId && order.ClientId == clientId
                       && (!sellerId.HasValue || order.SellerId == sellerId.Value)
                       && (order.PaidAt ?? order.ChannelCreatedAt ?? order.ImportedAt) >= rangeFrom
                       && (order.PaidAt ?? order.ChannelCreatedAt ?? order.ImportedAt) <= rangeTo
                    orderby (order.PaidAt ?? order.ChannelCreatedAt ?? order.ImportedAt) descending
                    select new { order, state };

        var rows = await query.Take(500).ToListAsync(cancellationToken);
        return rows.Select(x => MapOrder(x.order, x.state)).ToList();
    }

    public async Task<ClientProfitabilityOrderDetailResult?> GetOrderAsync(
        string tenantId, Guid clientId, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await _db.MarketplaceOrders.AsNoTracking().FirstOrDefaultAsync(x =>
            x.Id == orderId && x.TenantId == tenantId && x.ClientId == clientId, cancellationToken);
        if (order == null) return null;
        var state = await _db.MarketplaceOrderFinancialStates.AsNoTracking().FirstOrDefaultAsync(x =>
            x.MarketplaceOrderId == orderId && x.TenantId == tenantId && x.ClientId == clientId, cancellationToken);
        if (state == null) return null;

        var activeIds = await _db.FinancialEconomicHeads.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientId == clientId)
            .Select(x => x.ActiveEntryId).ToListAsync(cancellationToken);
        var active = activeIds.ToHashSet();
        var entries = await _db.MarketplaceFinancialEntries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientId == clientId && x.MarketplaceOrderId == orderId)
            .OrderBy(x => x.EconomicOccurredAt).ThenBy(x => x.ObservedAt).ToListAsync(cancellationToken);

        var summary = MapOrder(order, state);
        return new ClientProfitabilityOrderDetailResult
        {
            OrderId = summary.OrderId, ExternalOrderId = summary.ExternalOrderId,
            SellerId = summary.SellerId, Provider = summary.Provider, EconomicDate = summary.EconomicDate,
            Maturity = summary.Maturity, GrossRevenueCents = summary.GrossRevenueCents,
            EstimatedEconomicNetCents = summary.EstimatedEconomicNetCents,
            ConfirmedValueCents = summary.ConfirmedValueCents, OperationalProfitCents = summary.OperationalProfitCents,
            UnallocatedCents = summary.UnallocatedCents, Coverage = summary.Coverage,
            IncompleteReasons = summary.IncompleteReasons,
            Entries = entries.Select(x => new ClientProfitabilityEntryResult
            {
                EntryId = x.Id, EntryType = x.EntryType, Layer = x.Layer, Status = x.Status,
                AmountCents = x.AmountCents, CurrencyId = x.CurrencyId, EconomicKey = x.EconomicKey,
                IsActiveHead = active.Contains(x.Id), SupersedesEntryId = x.SupersedesEntryId,
                EconomicOccurredAt = x.EconomicOccurredAt, FinancialConfirmedAt = x.FinancialConfirmedAt,
                ObservedAt = x.ObservedAt, SourceEndpoint = x.SourceEndpoint
            }).ToList()
        };
    }

    public async Task<SellerTaxProfileResult?> GetTaxAsync(string tenantId, Guid clientId, long sellerId, CancellationToken cancellationToken)
    {
        var profile = await _db.SellerTaxProfileVersions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientId == clientId && x.SellerId == sellerId && x.EffectiveFrom <= DateTimeOffset.UtcNow)
            .OrderByDescending(x => x.EffectiveFrom).ThenByDescending(x => x.Version).FirstOrDefaultAsync(cancellationToken);
        return profile == null ? null : MapTax(profile);
    }

    public async Task<SellerTaxProfileResult> UpdateTaxAsync(string tenantId, Guid clientId, UpdateSellerTaxProfileRequest request,
        string actorId, CancellationToken cancellationToken)
    {
        if (request.SellerId <= 0) throw new ArgumentException("SellerId inválido.");
        if (request.RateBasisPoints is < 0 or > 10_000) throw new ArgumentException("A alíquota deve ficar entre 0% e 100%.");
        var allowed = await _db.TenantMarketplaceConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId
            && x.ClientId == clientId && x.SellerId == request.SellerId, cancellationToken);
        if (!allowed) throw new InvalidOperationException("Seller não pertence ao cliente autenticado.");
        var version = await _db.SellerTaxProfileVersions.Where(x => x.TenantId == tenantId && x.ClientId == clientId
                && x.SellerId == request.SellerId).MaxAsync(x => (long?)x.Version, cancellationToken) ?? 0;
        var profile = new SellerTaxProfileVersion
        {
            TenantId = tenantId, ClientId = clientId, SellerId = request.SellerId,
            RateBasisPoints = request.RateBasisPoints, EffectiveFrom = request.EffectiveFrom ?? DateTimeOffset.UtcNow,
            Version = version + 1, CreatedBy = actorId
        };
        _db.SellerTaxProfileVersions.Add(profile);
        await _db.SaveChangesAsync(cancellationToken);
        return MapTax(profile);
    }

    private async Task<int> ResolveTaxRateAsync(string tenantId, Guid clientId, long? sellerId, DateTimeOffset at, CancellationToken ct)
    {
        if (!sellerId.HasValue) return 0;
        return await _db.SellerTaxProfileVersions.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClientId == clientId
                && x.SellerId == sellerId && x.EffectiveFrom <= at)
            .OrderByDescending(x => x.EffectiveFrom).ThenByDescending(x => x.Version)
            .Select(x => x.RateBasisPoints).FirstOrDefaultAsync(ct);
    }

    private static FinancialCoverageResult BuildCoverage(IReadOnlyCollection<MarketplaceOrderFinancialState> states) => new()
    {
        SkuPercent = Percent(states, x => x.SkuResolved),
        CostPercent = Percent(states, x => x.CostResolved),
        FreightPercent = Percent(states, x => x.FreightResolved),
        OperationalPercent = Percent(states, x => x.OperationalComponentsResolved),
        ConfirmedPercent = Percent(states, x => x.ConfirmedComponentsResolved),
        ItemAllocationPercent = Percent(states, x => x.ItemAllocationResolved),
        OverallPercent = Percent(states, x => x.OperationalComponentsResolved && x.ConfirmedComponentsResolved && x.ItemAllocationResolved)
    };

    private static ClientProfitabilityOrderResult MapOrder(MarketplaceOrder order, MarketplaceOrderFinancialState state) => new()
    {
        OrderId = order.Id, ExternalOrderId = order.MlOrderId, SellerId = order.SellerId,
        Provider = order.Provider.ToString(), EconomicDate = order.PaidAt ?? order.ChannelCreatedAt ?? order.ImportedAt,
        Maturity = state.Maturity, GrossRevenueCents = state.GrossRevenueCents,
        EstimatedEconomicNetCents = state.EstimatedEconomicNetCents, ConfirmedValueCents = state.ConfirmedValueCents,
        OperationalProfitCents = state.OperationalProfitCents, UnallocatedCents = state.UnallocatedCents,
        Coverage = BuildCoverage([state]), IncompleteReasons = ReadReasons(state).ToList()
    };

    private static decimal Percent(IReadOnlyCollection<MarketplaceOrderFinancialState> states, Func<MarketplaceOrderFinancialState, bool> predicate)
        => states.Count == 0 ? 0 : Math.Round(states.Count(predicate) * 100m / states.Count, 1);
    private static IEnumerable<string> ReadReasons(MarketplaceOrderFinancialState state)
    {
        try { return JsonSerializer.Deserialize<List<string>>(state.IncompleteReasonsJson) ?? []; }
        catch (JsonException) { return ["INVALID_PROJECTION_METADATA"]; }
    }
    private static string AggregateMaturity(IReadOnlyCollection<MarketplaceOrderFinancialState> states)
    {
        if (states.Count == 0 || states.Any(x => x.Maturity == FinancialMaturity.Incomplete)) return FinancialMaturity.Incomplete;
        if (states.Any(x => x.Maturity == FinancialMaturity.Reopened)) return FinancialMaturity.Reopened;
        if (states.Any(x => x.Maturity == FinancialMaturity.PartiallyConfirmed)) return FinancialMaturity.PartiallyConfirmed;
        if (states.Any(x => x.Maturity == FinancialMaturity.Estimated)) return FinancialMaturity.Estimated;
        return FinancialMaturity.Confirmed;
    }
    private static string ToComponent(string type) => type switch
    {
        FinancialEntryTypes.SaleFee => "commission",
        FinancialEntryTypes.SellerShippingCost or FinancialEntryTypes.ShippingDiscountOrCompensation => "shipping",
        FinancialEntryTypes.Refund => "refunds",
        FinancialEntryTypes.BuyerDiscount => "discounts",
        FinancialEntryTypes.FinancingOrFixedFee => "fixed_or_financing_fees",
        FinancialEntryTypes.PlatformAdjustment => "adjustments",
        _ => "other"
    };
    private static SellerTaxProfileResult MapTax(SellerTaxProfileVersion x) => new()
    { SellerId = x.SellerId, RateBasisPoints = x.RateBasisPoints, EffectiveFrom = x.EffectiveFrom, Version = x.Version };
}
