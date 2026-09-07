using Microsoft.EntityFrameworkCore;
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
    {
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
                            && (order.ChannelCreatedAt ?? order.ImportedAt) <= rangeTo)
            .ToListAsync(cancellationToken);

        var current = orders
            .Where(order => EffectiveDate(order) >= rangeFrom && EffectiveDate(order) <= rangeTo)
            .ToList();
        var previous = orders
            .Where(order => EffectiveDate(order) >= previousFrom && EffectiveDate(order) < rangeFrom)
            .ToList();

        var currentPaid = current.Where(IsRevenueOrder).ToList();
        var previousPaid = previous.Where(IsRevenueOrder).ToList();
        var grossRevenue = currentPaid.Sum(OrderRevenue);
        var previousRevenue = previousPaid.Sum(OrderRevenue);
        var fees = currentPaid.SelectMany(order => order.Items).Sum(item => item.SaleFee ?? 0m);
        var totalUnits = currentPaid.SelectMany(order => order.Items).Sum(item => item.Quantity);

        var products = currentPaid
            .SelectMany(order => order.Items.Select(item => new { order.Id, Item = item }))
            .GroupBy(row => ResolveProductKey(row.Item), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ClientSalesSkuResult
            {
                ChannelItemId = group.First().Item.MlItemId,
                ChannelVariationId = group.First().Item.MlVariationId,
                Sku = ResolveDisplaySku(group.First().Item),
                ProductName = group.Select(row => row.Item.ProductName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                Orders = group.Select(row => row.Id).Distinct().Count(),
                Units = group.Sum(row => row.Item.Quantity),
                Revenue = Math.Round(group.Sum(row => ItemRevenue(row.Item)), 2),
                IsMapped = group.All(row => !string.IsNullOrWhiteSpace(row.Item.SabrVariantSku))
            })
            .OrderByDescending(row => row.Units)
            .ThenByDescending(row => row.Revenue)
            .ThenBy(row => row.ProductName)
            .ToList();

        var dailyLookup = currentPaid
            .GroupBy(order => DateOnly.FromDateTime(EffectiveDate(order).UtcDateTime.Date))
            .ToDictionary(
                group => group.Key,
                group => new ClientSalesDailyResult
                {
                    Date = group.Key,
                    Orders = group.Count(),
                    Units = group.SelectMany(order => order.Items).Sum(item => item.Quantity),
                    Revenue = Math.Round(group.Sum(OrderRevenue), 2)
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

        return new ClientSalesDashboardResult
        {
            From = rangeFrom,
            To = rangeTo,
            GeneratedAt = now,
            LastSyncedAt = lastSyncedAt,
            CurrencyId = currentPaid.Select(order => order.CurrencyId).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "BRL",
            TotalOrders = current.Count,
            PaidOrders = currentPaid.Count,
            TotalUnits = totalUnits,
            GrossRevenue = Math.Round(grossRevenue, 2),
            MarketplaceFees = Math.Round(fees, 2),
            NetRevenue = Math.Round(grossRevenue - fees, 2),
            AverageTicket = currentPaid.Count == 0 ? 0 : Math.Round(grossRevenue / currentPaid.Count, 2),
            CancelledOrders = current.Count(order => NormalizeStatus(order.Status).Contains("cancel", StringComparison.Ordinal)),
            UnmappedUnits = currentPaid.SelectMany(order => order.Items)
                .Where(item => string.IsNullOrWhiteSpace(item.SabrVariantSku))
                .Sum(item => item.Quantity),
            OrdersChangePercent = PercentageChange(current.Count, previous.Count),
            RevenueChangePercent = PercentageChange(grossRevenue, previousRevenue),
            DailySales = dailySales,
            TotalProducts = products.Count,
            Products = products,
            TopSkus = products.Take(10).ToList(),
            Statuses = statuses
        };
    }

    private static DateTimeOffset EffectiveDate(MarketplaceOrder order) => order.ChannelCreatedAt ?? order.ImportedAt;

    private static bool IsRevenueOrder(MarketplaceOrder order) => RevenueStatuses.Contains(NormalizeStatus(order.Status));

    private static decimal OrderRevenue(MarketplaceOrder order)
        => order.TotalAmount ?? order.Items.Sum(ItemRevenue);

    private static decimal ItemRevenue(MarketplaceOrderItem item)
        => (item.UnitPrice ?? item.FullUnitPrice ?? 0m) * item.Quantity;

    private static string ResolveProductKey(MarketplaceOrderItem item)
        => !string.IsNullOrWhiteSpace(item.SabrVariantSku)
            ? $"sabr:{item.SabrVariantSku.Trim()}"
            : !string.IsNullOrWhiteSpace(item.ChannelSku)
                ? $"channel:{item.ChannelSku.Trim()}"
                : $"item:{item.MlItemId.Trim()}:{item.MlVariationId?.Trim() ?? "base"}";

    private static string ResolveDisplaySku(MarketplaceOrderItem item)
        => item.SabrVariantSku?.Trim()
           ?? item.ChannelSku?.Trim()
           ?? $"SEM SKU · {item.MlItemId}{(string.IsNullOrWhiteSpace(item.MlVariationId) ? string.Empty : $"/{item.MlVariationId}")}";

    private static string NormalizeStatus(string? status)
        => string.IsNullOrWhiteSpace(status) ? "unknown" : status.Trim().ToLowerInvariant();

    private static decimal PercentageChange(decimal current, decimal previous)
    {
        if (previous == 0)
        {
            return current == 0 ? 0 : 100;
        }

        return Math.Round((current - previous) * 100m / previous, 1);
    }
}
