using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class MercadoLivreIntegrationService
{
    private readonly IAppDbContext _dbContext;
    private readonly IMercadoLivreApiClient _mercadoLivreApiClient;
    private readonly ILogger<MercadoLivreIntegrationService> _logger;

    public MercadoLivreIntegrationService(
        IAppDbContext dbContext,
        IMercadoLivreApiClient mercadoLivreApiClient,
        ILogger<MercadoLivreIntegrationService> logger)
    {
        _dbContext = dbContext;
        _mercadoLivreApiClient = mercadoLivreApiClient;
        _logger = logger;
    }

    public async Task<MercadoLivreIntegrationStatusResult> GetClientStatusAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var connections = await _dbContext.TenantMarketplaceConnections
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == MarketplaceProvider.MercadoLivre)
            .OrderBy(item => item.SellerId)
            .ToListAsync(cancellationToken);

        var mappingsCount = await _dbContext.TenantMarketplaceListingMaps
            .AsNoTracking()
            .CountAsync(item => item.TenantId == tenantId
                                && item.ClientId == clientId
                                && item.Provider == MarketplaceProvider.MercadoLivre,
                cancellationToken);

        var ordersCount = await _dbContext.MarketplaceOrders
            .AsNoTracking()
            .CountAsync(item => item.TenantId == tenantId
                                && item.ClientId == clientId
                                && item.Provider == MarketplaceProvider.MercadoLivre,
                cancellationToken);

        var unmappedItemsCount = await _dbContext.MarketplaceOrderItems
            .AsNoTracking()
            .CountAsync(item => item.TenantId == tenantId
                                && item.ClientId == clientId
                                && item.Provider == MarketplaceProvider.MercadoLivre
                                && item.MappingState == MarketplaceMappingStates.Unmapped,
                cancellationToken);

        return new MercadoLivreIntegrationStatusResult
        {
            Connected = connections.Count > 0,
            Connections = connections.Select(item => new MercadoLivreConnectionStatusResult
            {
                IntegrationId = item.Id,
                SellerId = MercadoLivreSellerIdParser.ToApiString(item.SellerId),
                Nickname = item.Nickname,
                TokenExpiresAt = item.TokenExpiresAt,
                LastSyncAt = item.LastSyncAt
            }).ToList(),
            MappingsCount = mappingsCount,
            OrdersCount = ordersCount,
            UnmappedItemsCount = unmappedItemsCount,
            LastSyncAt = connections.MaxBy(item => item.LastSyncAt ?? DateTimeOffset.MinValue)?.LastSyncAt
        };
    }

    public async Task<ServiceResult<bool>> DisconnectAsync(
        string tenantId,
        Guid clientId,
        string? sellerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("context", "Invalid tenant/client context")
            });
        }

        var connectionsQuery = _dbContext.TenantMarketplaceConnections
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == MarketplaceProvider.MercadoLivre);
        if (!MercadoLivreSellerIdParser.TryParseOptional(sellerId, out var normalizedSeller))
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("sellerId", "SellerId must be numeric")
            });
        }

        if (normalizedSeller.HasValue)
        {
            connectionsQuery = connectionsQuery.Where(item => item.SellerId == normalizedSeller.Value);
        }

        var connections = await connectionsQuery.ToListAsync(cancellationToken);
        if (connections.Count == 0)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("sellerId", "Connection not found")
            });
        }

        var sellers = connections.Select(item => item.SellerId).ToList();
        var maps = await _dbContext.TenantMarketplaceListingMaps
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && sellers.Contains(item.SellerId))
            .ToListAsync(cancellationToken);

        _dbContext.TenantMarketplaceListingMaps.RemoveRange(maps);
        _dbContext.TenantMarketplaceConnections.RemoveRange(connections);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<bool>> ResetAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("context", "Invalid tenant/client context")
            });
        }

        var orderIds = await _dbContext.MarketplaceOrders
            .Where(o => o.TenantId == tenantId
                        && o.ClientId == clientId
                        && o.Provider == MarketplaceProvider.MercadoLivre)
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);

        if (orderIds.Count > 0)
        {
            var reservations = await _dbContext.StockReservations
                .Where(r => orderIds.Contains(r.MarketplaceOrderId))
                .ToListAsync(cancellationToken);
            _dbContext.StockReservations.RemoveRange(reservations);

            var shipments = await _dbContext.MarketplaceShipments
                .Where(s => s.TenantId == tenantId
                            && s.ClientId == clientId
                            && s.Provider == MarketplaceProvider.MercadoLivre)
                .ToListAsync(cancellationToken);
            _dbContext.MarketplaceShipments.RemoveRange(shipments);

            var items = await _dbContext.MarketplaceOrderItems
                .Where(i => orderIds.Contains(i.MarketplaceOrderId))
                .ToListAsync(cancellationToken);
            _dbContext.MarketplaceOrderItems.RemoveRange(items);

            var orders = await _dbContext.MarketplaceOrders
                .Where(o => orderIds.Contains(o.Id))
                .ToListAsync(cancellationToken);
            _dbContext.MarketplaceOrders.RemoveRange(orders);
        }

        var eventLogs = await _dbContext.MarketplaceEventLogs
            .Where(e => e.TenantId == tenantId
                        && e.ClientId == clientId
                        && e.Provider == MarketplaceProvider.MercadoLivre)
            .ToListAsync(cancellationToken);
        _dbContext.MarketplaceEventLogs.RemoveRange(eventLogs);

        var listingDrafts = await _dbContext.ListingDrafts
            .Where(d => d.TenantId == tenantId
                        && d.ClientId == clientId
                        && d.Provider == MarketplaceProvider.MercadoLivre)
            .ToListAsync(cancellationToken);
        _dbContext.ListingDrafts.RemoveRange(listingDrafts);

        var slaRules = await _dbContext.TenantMarketplaceSlaRules
            .Where(r => r.TenantId == tenantId
                        && r.ClientId == clientId
                        && r.Provider == MarketplaceProvider.MercadoLivre)
            .ToListAsync(cancellationToken);
        _dbContext.TenantMarketplaceSlaRules.RemoveRange(slaRules);

        var listingMaps = await _dbContext.TenantMarketplaceListingMaps
            .Where(m => m.TenantId == tenantId
                        && m.ClientId == clientId
                        && m.Provider == MarketplaceProvider.MercadoLivre)
            .ToListAsync(cancellationToken);
        _dbContext.TenantMarketplaceListingMaps.RemoveRange(listingMaps);

        var connections = await _dbContext.TenantMarketplaceConnections
            .Where(c => c.TenantId == tenantId
                        && c.ClientId == clientId
                        && c.Provider == MarketplaceProvider.MercadoLivre)
            .ToListAsync(cancellationToken);
        _dbContext.TenantMarketplaceConnections.RemoveRange(connections);

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "ML integration reset. tenantId={TenantId} clientId={ClientId} orders={Orders} drafts={Drafts} maps={Maps}",
            tenantId, clientId, orderIds.Count, listingDrafts.Count, listingMaps.Count);

        return ServiceResult<bool>.Success(true);
    }

    public async Task<PagedResult<MarketplaceOrderListItemResult>> ListOrdersAsync(
        string tenantId,
        Guid clientId,
        MarketplaceProvider provider,
        string? status,
        string? logisticType,
        int skip,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var safeSkip = Math.Max(0, skip);
        var safeLimit = Math.Min(200, Math.Max(1, limit));

        var query = _dbContext.MarketplaceOrders
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == provider);

        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalizedStatus = status.Trim().ToLowerInvariant();
            query = query.Where(item => item.Status.ToLower() == normalizedStatus);
        }

        if (!string.IsNullOrWhiteSpace(logisticType))
        {
            var normalizedLogisticType = logisticType.Trim().ToLowerInvariant();
            query = query.Where(item => item.LogisticType != null && item.LogisticType.ToLower() == normalizedLogisticType);
        }

        var total = await query.CountAsync(cancellationToken);
        var page = await query
            .OrderByDescending(item => item.ImportedAt)
            .Skip(safeSkip)
            .Take(safeLimit)
            .Select(item => new MarketplaceOrderListItemResult
            {
                Id = item.Id,
                Provider = item.Provider,
                SellerId = MercadoLivreSellerIdParser.ToApiString(item.SellerId),
                MlOrderId = item.MlOrderId,
                Status = item.Status,
                PaidAt = item.PaidAt,
                SabrPaymentConfirmedAt = item.SabrPaymentConfirmedAt,
                ShippingMode = item.ShippingMode,
                LogisticType = item.LogisticType,
                ShipByDeadlineAt = item.ShipByDeadlineAt,
                RiskFlagsJson = item.RiskFlagsJson,
                ImportedAt = item.ImportedAt
            })
            .ToListAsync(cancellationToken);

        var orderIds = page.Select(item => item.Id).ToList();
        var itemGroups = await _dbContext.MarketplaceOrderItems
            .AsNoTracking()
            .Where(item => orderIds.Contains(item.MarketplaceOrderId))
            .GroupBy(item => item.MarketplaceOrderId)
            .Select(group => new
            {
                OrderId = group.Key,
                Total = group.Count(),
                Reserved = group.Sum(i => i.ReservedQuantity),
                Unmapped = group.Any(i => i.MappingState == MarketplaceMappingStates.Unmapped)
            })
            .ToListAsync(cancellationToken);
        var groupByOrder = itemGroups.ToDictionary(item => item.OrderId);

        foreach (var row in page)
        {
            if (!groupByOrder.TryGetValue(row.Id, out var agg))
            {
                continue;
            }

            row.TotalItems = agg.Total;
            row.ReservedItems = agg.Reserved;
            row.HasUnmappedItems = agg.Unmapped;
        }

        return new PagedResult<MarketplaceOrderListItemResult>
        {
            Items = page,
            Total = total,
            Skip = safeSkip,
            Limit = safeLimit
        };
    }

    public async Task<ServiceResult<MercadoLivreIntegrationStatusResult>> GetAdminStatusAsync(
        string tenantSlug,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantSlug) || clientId == Guid.Empty)
        {
            return ServiceResult<MercadoLivreIntegrationStatusResult>.Failure(new[]
            {
                new ValidationError("context", "Invalid tenant/client context")
            });
        }

        var normalizedSlug = tenantSlug.Trim().ToLowerInvariant();
        var tenant = await _dbContext.Tenants.AsNoTracking().FirstOrDefaultAsync(
            item => item.Slug == normalizedSlug,
            cancellationToken);
        if (tenant == null)
        {
            return ServiceResult<MercadoLivreIntegrationStatusResult>.Failure(new[]
            {
                new ValidationError("tenantSlug", "Tenant not found")
            });
        }

        var clientExists = await _dbContext.Clients.AsNoTracking().AnyAsync(
            item => item.TenantId == tenant.Id && item.Id == clientId,
            cancellationToken);
        if (!clientExists)
        {
            return ServiceResult<MercadoLivreIntegrationStatusResult>.Failure(new[]
            {
                new ValidationError("clientId", "Client not found")
            });
        }

        var status = await GetClientStatusAsync(tenant.Id, clientId, cancellationToken);
        return ServiceResult<MercadoLivreIntegrationStatusResult>.Success(status);
    }
}
