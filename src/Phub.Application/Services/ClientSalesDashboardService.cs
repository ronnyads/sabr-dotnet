using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class ClientSalesDashboardService
{
    private static readonly HashSet<string> RevenueStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "paid"
    };

    private readonly IAppDbContext _dbContext;

    public ClientSalesDashboardService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ClientSalesDashboardResult> GetAsync(
        string tenantId,
        Guid clientId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        MarketplaceProvider? provider,
        CancellationToken cancellationToken = default)
        => await GetAsync(tenantId, clientId, from, to, provider, null, cancellationToken);

    public async Task<ClientSalesDashboardResult> GetAsync(
        string tenantId,
        Guid clientId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        MarketplaceProvider? provider,
        string? supplierScope,
        CancellationToken cancellationToken = default)
    {
        supplierScope = NormalizeSupplierScope(supplierScope);
        var now = DateTimeOffset.UtcNow;
        var rangeTo = (to ?? now).ToUniversalTime();
        var rangeFrom = (from ?? rangeTo.AddDays(-29)).ToUniversalTime();
        if (rangeFrom > rangeTo)
        {
            (rangeFrom, rangeTo) = (rangeTo, rangeFrom);
        }

        if (rangeTo - rangeFrom > TimeSpan.FromDays(366))
        {
            rangeFrom = rangeTo.AddDays(-366);
        }

        var rangeDuration = rangeTo - rangeFrom;
        var previousFrom = rangeFrom - rangeDuration;

        var baseQuery = _dbContext.MarketplaceOrders
            .AsNoTracking()
            .Include(order => order.Items)
            .Where(order => order.TenantId == tenantId && order.ClientId == clientId);

        if (provider.HasValue)
        {
            baseQuery = baseQuery.Where(order => order.Provider == provider.Value);
        }

        var orders = await baseQuery
            .Where(order => (order.ChannelCreatedAt ?? order.ImportedAt) >= previousFrom
                            && (order.ChannelCreatedAt ?? order.ImportedAt) < rangeTo)
            .ToListAsync(cancellationToken);

        var unfilteredCurrent = orders
            .Where(order => EffectiveDate(order) >= rangeFrom && EffectiveDate(order) < rangeTo)
            .ToList();
        var unfilteredPrevious = orders
            .Where(order => EffectiveDate(order) >= previousFrom && EffectiveDate(order) < rangeFrom)
            .ToList();

        var supplierFilters = BuildSupplierFilters(unfilteredCurrent.Where(IsRevenueOrder));
        var current = unfilteredCurrent.Where(order => order.Items.Any(item => MatchesSupplierScope(item, supplierScope))).ToList();
        var previous = unfilteredPrevious.Where(order => order.Items.Any(item => MatchesSupplierScope(item, supplierScope))).ToList();

        var currentPaid = current.Where(IsRevenueOrder).ToList();
        var previousPaid = previous.Where(IsRevenueOrder).ToList();
        var includedPaid = currentPaid.Where(order => order.Items.Any(item => IsIncludedInSalesResult(item) && MatchesSupplierScope(item, supplierScope))).ToList();
        var previousIncludedPaid = previousPaid.Where(order => order.Items.Any(item => IsIncludedInSalesResult(item) && MatchesSupplierScope(item, supplierScope))).ToList();
        var grossRevenue = includedPaid.SelectMany(order => order.Items)
            .Where(item => IsIncludedInSalesResult(item) && MatchesSupplierScope(item, supplierScope)).Sum(ItemRevenue);
        var totalSalesAmount = current.SelectMany(order => order.Items).Where(item => MatchesSupplierScope(item, supplierScope)).Sum(ItemRevenue);
        var cancelledInPeriod = current
            .Where(IsCommercialCancellation)
            .Where(order => order.Items.Any(item => MatchesSupplierScope(item, supplierScope)))
            .ToList();
        var cancelledSalesAmount = cancelledInPeriod
            .SelectMany(order => order.Items).Where(item => MatchesSupplierScope(item, supplierScope)).Sum(ItemRevenue);
        var previousRevenue = previousIncludedPaid.SelectMany(order => order.Items)
            .Where(item => IsIncludedInSalesResult(item) && MatchesSupplierScope(item, supplierScope)).Sum(ItemRevenue);
        var fees = includedPaid.SelectMany(order => order.Items)
            .Where(item => IsIncludedInSalesResult(item) && MatchesSupplierScope(item, supplierScope)).Sum(item => item.SaleFee ?? 0m);
        var totalUnits = current.SelectMany(order => order.Items)
            .Where(item => MatchesSupplierScope(item, supplierScope)).Sum(item => item.Quantity);
        var paidUnits = includedPaid.SelectMany(order => order.Items)
            .Where(item => IsIncludedInSalesResult(item) && MatchesSupplierScope(item, supplierScope)).Sum(item => item.Quantity);
        var cancelledUnits = current.Where(order => IsCancelled(order.Status)).SelectMany(order => order.Items)
            .Where(item => MatchesSupplierScope(item, supplierScope)).Sum(item => item.Quantity);
        var refundedUnits = current.Where(order => NormalizeStatus(order.Status) is "refunded" or "partially_refunded")
            .SelectMany(order => order.Items).Where(item => MatchesSupplierScope(item, supplierScope)).Sum(item => item.Quantity);

        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, ResolveSaoPauloTimeZone()).Date);
        var products = currentPaid
            .SelectMany(order => order.Items.Select(item => new { Order = order, Item = item }))
            .Where(row => MatchesSupplierScope(row.Item, supplierScope))
            .GroupBy(row => ResolveProductKey(row.Item), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ClientSalesSkuResult
            {
                SellerId = group.First().Item.SellerId,
                ChannelItemId = group.First().Item.MlItemId,
                ChannelVariationId = group.First().Item.MlVariationId,
                Sku = ResolveDisplaySku(group.First().Item),
                ProductName = group.Select(row => row.Item.ProductName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                Orders = group.Select(row => row.Order.Id).Distinct().Count(),
                Units = group.Sum(row => row.Item.Quantity),
                Revenue = Math.Round(group.Sum(row => ItemRevenue(row.Item)), 2),
                IsMapped = group.All(row => !string.IsNullOrWhiteSpace(row.Item.SabrVariantSku)),
                IsExternalSupplier = group.All(row => IsExternalSupplier(row.Item)),
                HasExternalCost = group.All(row => row.Item.ExternalUnitCostCentsSnapshot.HasValue),
                ExternalSupplierName = group.Select(row => row.Item.ExternalSupplierName)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                ExternalUnitCostCents = group.Select(row => row.Item.ExternalUnitCostCentsSnapshot).FirstOrDefault(value => value.HasValue),
                ExternalCurrencyId = group.Select(row => row.Item.ExternalCostCurrencyId).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                OverdueOrders = group.Where(row => !row.Order.SabrPaymentConfirmedAt.HasValue
                    && row.Order.ShipByDeadlineAt.HasValue && row.Order.ShipByDeadlineAt.Value < now)
                    .Select(row => row.Order.Id).Distinct().Count(),
                DueTodayOrders = group.Where(row => !row.Order.SabrPaymentConfirmedAt.HasValue
                    && row.Order.ShipByDeadlineAt.HasValue
                    && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(row.Order.ShipByDeadlineAt.Value, ResolveSaoPauloTimeZone()).Date) == localToday)
                    .Select(row => row.Order.Id).Distinct().Count(),
                DueTodayUnits = group.Where(row => !row.Order.SabrPaymentConfirmedAt.HasValue
                    && row.Order.ShipByDeadlineAt.HasValue
                    && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(row.Order.ShipByDeadlineAt.Value, ResolveSaoPauloTimeZone()).Date) == localToday)
                    .Sum(row => row.Item.Quantity),
                EarliestDeadlineAt = group.Where(row => !row.Order.SabrPaymentConfirmedAt.HasValue)
                    .Min(row => row.Order.ShipByDeadlineAt)
            })
            .OrderByDescending(row => !row.IsMapped && row.OverdueOrders > 0)
            .ThenByDescending(row => !row.IsMapped && row.DueTodayOrders > 0)
            .ThenBy(row => row.IsMapped ? DateTimeOffset.MaxValue : row.EarliestDeadlineAt ?? DateTimeOffset.MaxValue)
            .ThenByDescending(row => row.Orders)
            .ThenByDescending(row => row.Units)
            .ThenByDescending(row => row.Revenue)
            .ThenBy(row => row.ProductName)
            .ToList();
        foreach (var product in products.Where(product => !product.IsMapped && !product.IsExternalSupplier))
        {
            product.MappingPriority = product.OverdueOrders > 0 ? "OVERDUE"
                : product.DueTodayOrders > 0 ? "DUE_TODAY" : "PENDING";
            product.MappingReason = product.OverdueOrders > 0 ? "Pedido com prazo vencido"
                : product.DueTodayOrders > 0 ? "Pedido para enviar hoje" : "Produto vendido sem SKU interno";
        }
        foreach (var product in products.Where(product => product.IsExternalSupplier))
        {
            product.MappingPriority = product.HasExternalCost ? MarketplaceMappingStates.ExternalSupplier : MarketplaceMappingStates.ExternalCostPending;
            product.MappingReason = product.HasExternalCost
                ? "Produto de fornecedor externo com custo informado"
                : "Informe o custo do produto externo para concluir a apuração";
        }

        var externalProducts = products.Where(product => product.IsExternalSupplier).ToList();
        var externalOrderIds = currentPaid
            .Where(order => order.Items.Any(IsExternalSupplier))
            .Select(order => order.Id)
            .Distinct()
            .Count();

        var dailyLookup = includedPaid
            .GroupBy(order => DateOnly.FromDateTime(EffectiveDate(order).UtcDateTime.Date))
            .ToDictionary(
                group => group.Key,
                group => new ClientSalesDailyResult
                {
                    Date = group.Key,
                    Orders = group.Count(),
                    Units = group.SelectMany(order => order.Items)
                        .Where(item => IsIncludedInSalesResult(item) && MatchesSupplierScope(item, supplierScope)).Sum(item => item.Quantity),
                    Revenue = Math.Round(group.SelectMany(order => order.Items)
                        .Where(item => IsIncludedInSalesResult(item) && MatchesSupplierScope(item, supplierScope)).Sum(ItemRevenue), 2)
                });

        var dailySales = new List<ClientSalesDailyResult>();
        for (var date = DateOnly.FromDateTime(rangeFrom.UtcDateTime.Date);
             date <= DateOnly.FromDateTime(rangeTo.UtcDateTime.Date);
             date = date.AddDays(1))
        {
            dailySales.Add(dailyLookup.GetValueOrDefault(date) ?? new ClientSalesDailyResult { Date = date });
        }

        var statuses = current
            .GroupBy(order => NormalizeStatus(order.Status), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ClientSalesStatusResult
            {
                Status = group.Key,
                Orders = group.Count(),
                Percentage = current.Count == 0 ? 0 : Math.Round(group.Count() * 100m / current.Count, 1)
            })
            .OrderByDescending(row => row.Orders)
            .ToList();

        var lastSyncedAt = await _dbContext.TenantMarketplaceConnections
            .AsNoTracking()
            .Where(connection => connection.TenantId == tenantId && connection.ClientId == clientId)
            .Where(connection => !provider.HasValue || connection.Provider == provider.Value)
            .MaxAsync(connection => (DateTimeOffset?)connection.LastSyncAt, cancellationToken);

        var shippingToday = await BuildShippingTodayAsync(
            tenantId,
            clientId,
            provider,
            now,
            supplierScope,
            cancellationToken);

        return new ClientSalesDashboardResult
        {
            From = rangeFrom,
            To = rangeTo,
            GeneratedAt = now,
            LastSyncedAt = lastSyncedAt,
            CurrencyId = currentPaid.Select(order => order.CurrencyId).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "BRL",
            TotalOrders = current.Count,
            PaidOrders = includedPaid.Count,
            TotalSalesAmount = Math.Round(totalSalesAmount, 2),
            CancelledSalesAmount = Math.Round(cancelledSalesAmount, 2),
            TotalUnits = totalUnits,
            PaidUnits = paidUnits,
            CancelledUnits = cancelledUnits,
            RefundedUnits = refundedUnits,
            GrossRevenue = Math.Round(grossRevenue, 2),
            MarketplaceFees = Math.Round(fees, 2),
            NetRevenue = Math.Round(grossRevenue - fees, 2),
            AverageTicket = includedPaid.Count == 0 ? 0 : Math.Round(grossRevenue / includedPaid.Count, 2),
            CancelledOrders = cancelledInPeriod.Select(ResolveMarketplaceSaleKey)
                .Distinct(StringComparer.Ordinal).Count(),
            CurrentStatusCancelledOrders = current.Count(order => IsCancelled(order.Status)),
            CancellationTimestampPendingOrders = current.Count(order => IsCancelled(order.Status)
                && string.IsNullOrWhiteSpace(ResolveCancellationGroup(order))),
            RefundedOrders = current.Count(order => NormalizeStatus(order.Status) is "refunded" or "partially_refunded"),
            UnmappedUnits = currentPaid.SelectMany(order => order.Items)
                .Where(item => MatchesSupplierScope(item, supplierScope)
                               && string.IsNullOrWhiteSpace(item.SabrVariantSku) && !IsExternalSupplier(item))
                .Sum(item => item.Quantity),
            OrdersChangePercent = PercentageChange(current.Count, previous.Count),
            RevenueChangePercent = PercentageChange(grossRevenue, previousRevenue),
            DailySales = dailySales,
            TotalProducts = products.Count,
            Products = products,
            TopSkus = products.Where(product => !product.IsExternalSupplier || product.HasExternalCost).Take(10).ToList(),
            Statuses = statuses,
            ShippingToday = shippingToday
            ,ExternalSupplier = new ExternalSupplierSalesSummary
            {
                Products = externalProducts.Count,
                Orders = externalOrderIds,
                Units = externalProducts.Sum(product => product.Units),
                GrossRevenue = Math.Round(externalProducts.Sum(product => product.Revenue), 2),
                ProductsWithCost = externalProducts.Count(product => product.HasExternalCost),
                ProductsPendingCost = externalProducts.Count(product => !product.HasExternalCost)
            },
            SupplierScope = supplierScope ?? "ALL",
            SupplierFilters = supplierFilters
        };
    }

    private async Task<ClientShippingTodayResult> BuildShippingTodayAsync(
        string tenantId,
        Guid clientId,
        MarketplaceProvider? provider,
        DateTimeOffset nowUtc,
        string? supplierScope,
        CancellationToken cancellationToken)
    {
        var timeZone = ResolveSaoPauloTimeZone();
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, timeZone).Date);
        var localStart = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var localEnd = localDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var utcStart = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone));
        var utcEnd = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localEnd, timeZone));

        var dueQuery = _dbContext.MarketplaceOrders
            .AsNoTracking()
            .Include(order => order.Items)
            .Where(order => order.TenantId == tenantId
                            && order.ClientId == clientId
                            && order.ShipByDeadlineAt >= utcStart
                            && order.ShipByDeadlineAt < utcEnd);

        if (provider.HasValue)
        {
            dueQuery = dueQuery.Where(order => order.Provider == provider.Value);
        }

        var dueOrders = (await dueQuery.ToListAsync(cancellationToken))
            .Where(order => !IsCancelled(order.Status))
            .ToList();

        if (dueOrders.Count == 0)
        {
            return new ClientShippingTodayResult { DueDate = localDate };
        }

        var shipmentIds = dueOrders
            .Select(order => order.ShipmentId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var orderIds = dueOrders.Select(order => order.MlOrderId).Distinct(StringComparer.Ordinal).ToList();

        var externallyDispatchedShipmentIds = shipmentIds.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await _dbContext.MarketplaceShipments
                .AsNoTracking()
                .Where(shipment => shipment.TenantId == tenantId
                                   && shipment.ClientId == clientId
                                   && shipmentIds.Contains(shipment.ShipmentId))
                .Where(shipment => shipment.ShippedAt.HasValue
                                   || shipment.Status == "shipped"
                                   || shipment.Status == "delivered")
                .Select(shipment => shipment.ShipmentId)
                .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);

        var dispatchedResources = await _dbContext.MarketplaceEventLogs
            .AsNoTracking()
            .Where(log => log.TenantId == tenantId && log.ClientId == clientId)
            .Where(log => (log.Topic == MarketplaceEventTopics.AuditFulfillmentDispatched
                           && shipmentIds.Contains(log.ResourceId))
                          || (log.Topic == MarketplaceEventTopics.AuditOrderDispatched
                              && orderIds.Contains(log.ResourceId)))
            .Select(log => log.ResourceId)
            .ToListAsync(cancellationToken);
        var dispatched = dispatchedResources.ToHashSet(StringComparer.Ordinal);

        var pendingOrders = dueOrders
            .Where(order => !externallyDispatchedShipmentIds.Contains(order.ShipmentId ?? string.Empty))
            .Where(order => !dispatched.Contains(order.ShipmentId ?? string.Empty)
                            && !dispatched.Contains(order.MlOrderId))
            .Where(order => order.Items.Any(item => MatchesSupplierScope(item, supplierScope)))
            .ToList();

        var products = pendingOrders
            .SelectMany(order => order.Items.Select(item => new { order.Id, Item = item }))
            .Where(row => MatchesSupplierScope(row.Item, supplierScope))
            .GroupBy(row => ResolveProductKey(row.Item), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ClientShippingTodaySkuResult
            {
                Sku = ResolveDisplaySku(group.First().Item),
                ProductName = group.Select(row => row.Item.ProductName)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                Orders = group.Select(row => row.Id).Distinct().Count(),
                Units = group.Sum(row => row.Item.Quantity),
                IsMapped = group.All(row => !string.IsNullOrWhiteSpace(row.Item.SabrVariantSku))
            })
            .OrderByDescending(row => row.Units)
            .ThenBy(row => row.ProductName)
            .ToList();

        return new ClientShippingTodayResult
        {
            DueDate = localDate,
            TotalOrders = pendingOrders.Count,
            PaidOrders = pendingOrders.Count(order => order.SabrPaymentConfirmedAt.HasValue),
            PendingPaymentOrders = pendingOrders.Count(order => !order.SabrPaymentConfirmedAt.HasValue),
            TotalUnits = products.Sum(product => product.Units),
            UnmappedUnits = pendingOrders.SelectMany(order => order.Items)
                .Where(item => MatchesSupplierScope(item, supplierScope) && string.IsNullOrWhiteSpace(item.SabrVariantSku))
                .Sum(item => item.Quantity),
            Products = products
        };
    }

    private static DateTimeOffset EffectiveDate(MarketplaceOrder order) => order.ChannelCreatedAt ?? order.ImportedAt;

    private static bool IsRevenueOrder(MarketplaceOrder order) => RevenueStatuses.Contains(NormalizeStatus(order.Status));

    private static decimal OrderRevenue(MarketplaceOrder order)
        => order.TotalAmount ?? order.Items.Sum(ItemRevenue);

    private static decimal ItemRevenue(MarketplaceOrderItem item)
        => (item.UnitPrice ?? item.FullUnitPrice ?? 0m) * item.Quantity;

    private static string ResolveProductKey(MarketplaceOrderItem item)
    {
        // Different marketplace listings are only channel identities. Once they are
        // mapped, the internal SKU is the product identity used by the dashboard.
        // Unmapped/external items intentionally remain isolated by listing and
        // variation so unrelated products are never collapsed by assumption.
        if (!string.IsNullOrWhiteSpace(item.SabrVariantSku))
            return $"sku:{item.SabrVariantSku.Trim().ToUpperInvariant()}";

        return $"seller:{item.SellerId}:item:{item.MlItemId.Trim()}:variation:{item.MlVariationId?.Trim() ?? "base"}";
    }

    private static string ResolveDisplaySku(MarketplaceOrderItem item)
        => item.SabrVariantSku?.Trim()
           ?? item.ChannelSku?.Trim()
           ?? $"SEM SKU · {item.MlItemId}{(string.IsNullOrWhiteSpace(item.MlVariationId) ? string.Empty : $"/{item.MlVariationId}")}";

    private static string NormalizeStatus(string? status)
        => string.IsNullOrWhiteSpace(status) ? "unknown" : status.Trim().ToLowerInvariant();

    private static bool IsCancelled(string? status)
        => NormalizeStatus(status).Contains("cancel", StringComparison.Ordinal);

    private static bool IsCommercialCancellation(MarketplaceOrder order)
    {
        if (!IsCancelled(order.Status)) return false;
        return ResolveCancellationGroup(order) is "buyer" or "fraud" or "internal";
    }

    private static string ResolveMarketplaceSaleKey(MarketplaceOrder order)
        => ReadRawString(order.RawJson, "pack_id") ?? order.MlOrderId;

    private static string? ResolveCancellationGroup(MarketplaceOrder order)
    {
        if (string.IsNullOrWhiteSpace(order.RawJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(order.RawJson);
            if (!document.RootElement.TryGetProperty("cancel_detail", out var detail)
                || detail.ValueKind != JsonValueKind.Object)
                return null;
            return detail.TryGetProperty("group", out var group) && group.ValueKind == JsonValueKind.String
                ? group.GetString()?.Trim().ToLowerInvariant()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadRawString(string? rawJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (!document.RootElement.TryGetProperty(propertyName, out var value)) return null;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsExternalSupplier(MarketplaceOrderItem item)
        => MarketplaceMappingStates.IsExternal(item.MappingState);

    private static bool IsIncludedInSalesResult(MarketplaceOrderItem item)
        => item.MappingState != MarketplaceMappingStates.ExternalCostPending;

    private static string? NormalizeSupplierScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.Equals(scope, "ALL", StringComparison.OrdinalIgnoreCase)) return null;
        var trimmed = scope.Trim();
        if (trimmed.Equals("INTERNAL", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("EXTERNAL", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("UNCLASSIFIED", StringComparison.OrdinalIgnoreCase))
            return trimmed.ToUpperInvariant();
        return trimmed.StartsWith("EXTERNAL:", StringComparison.OrdinalIgnoreCase) ? trimmed : null;
    }

    private static bool MatchesSupplierScope(MarketplaceOrderItem item, string? scope)
    {
        if (scope == null) return true;
        if (scope == "INTERNAL") return !IsExternalSupplier(item) && !string.IsNullOrWhiteSpace(item.SabrVariantSku);
        if (scope == "EXTERNAL") return IsExternalSupplier(item);
        if (scope == "UNCLASSIFIED") return !IsExternalSupplier(item) && string.IsNullOrWhiteSpace(item.SabrVariantSku);
        if (scope.StartsWith("EXTERNAL:", StringComparison.OrdinalIgnoreCase))
            return IsExternalSupplier(item)
                   && string.Equals(item.ExternalSupplierName?.Trim(), scope[9..].Trim(), StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static List<ClientSupplierFilterOption> BuildSupplierFilters(IEnumerable<MarketplaceOrder> orders)
    {
        var items = orders.SelectMany(order => order.Items).ToList();
        var options = new List<ClientSupplierFilterOption>
        {
            new() { Key = "ALL", Label = "Todos os fornecedores", Origin = "ALL" }
        };
        if (items.Any(item => !IsExternalSupplier(item) && !string.IsNullOrWhiteSpace(item.SabrVariantSku)))
            options.Add(new ClientSupplierFilterOption { Key = "INTERNAL", Label = "Catálogo SABR", Origin = "INTERNAL" });
        var external = items.Where(IsExternalSupplier).ToList();
        if (external.Count > 0)
        {
            options.Add(new ClientSupplierFilterOption { Key = "EXTERNAL", Label = "Fornecedores externos · todos", Origin = "EXTERNAL" });
            options.AddRange(external.Select(item => item.ExternalSupplierName?.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name)
                .Select(name => new ClientSupplierFilterOption
                {
                    Key = $"EXTERNAL:{name}", Label = $"Externo · {name}", Origin = "EXTERNAL"
                }));
        }
        if (items.Any(item => !IsExternalSupplier(item) && string.IsNullOrWhiteSpace(item.SabrVariantSku)))
            options.Add(new ClientSupplierFilterOption { Key = "UNCLASSIFIED", Label = "Origem ainda não definida", Origin = "UNCLASSIFIED" });
        return options;
    }

    private static TimeZoneInfo ResolveSaoPauloTimeZone()
    {
        foreach (var id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // Try the equivalent identifier for the current operating system.
            }
            catch (InvalidTimeZoneException)
            {
                // Fall back to UTC only when the host has no valid São Paulo zone.
            }
        }

        return TimeZoneInfo.Utc;
    }

    private static decimal PercentageChange(decimal current, decimal previous)
    {
        if (previous == 0)
        {
            return current == 0 ? 0 : 100;
        }

        return Math.Round((current - previous) * 100m / previous, 1);
    }
}
