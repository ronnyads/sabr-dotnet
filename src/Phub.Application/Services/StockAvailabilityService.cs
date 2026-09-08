using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Options;
using Phub.Domain.Entities;
using System.Text.Json;

namespace Phub.Application.Services;

public sealed class StockAvailabilityService
{
    private readonly IAppDbContext _dbContext;
    private readonly IMercadoLivreApiClient _mercadoLivreApiClient;
    private readonly MercadoLivreOAuthService _oauthService;
    private readonly ILogger<StockAvailabilityService> _logger;
    private readonly MercadoLivreFeatureFlags _features;

    public StockAvailabilityService(
        IAppDbContext dbContext,
        IMercadoLivreApiClient mercadoLivreApiClient,
        MercadoLivreOAuthService oauthService,
        ILogger<StockAvailabilityService> logger,
        IOptions<MercadoLivreOptions> options)
    {
        _dbContext = dbContext;
        _mercadoLivreApiClient = mercadoLivreApiClient;
        _oauthService = oauthService;
        _logger = logger;
        _features = options.Value.Features;
    }

    public static int ComputeAvailable(ProductVariant variant)
    {
        return Math.Max(0, variant.PhysicalStock - variant.ReservedStock - variant.SafetyBuffer);
    }

    public async Task SyncStockForSkusAsync(
        string tenantId,
        Guid clientId,
        IEnumerable<string> variantSkus,
        CancellationToken cancellationToken = default)
    {
        var uniqueSkus = variantSkus
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (uniqueSkus.Count == 0)
        {
            return;
        }

        foreach (var sku in uniqueSkus)
        {
            await SyncStockForSkuAsync(tenantId, clientId, sku, cancellationToken);
        }
    }

    public async Task SyncStockForSkuAsync(
        string tenantId,
        Guid clientId,
        string variantSku,
        CancellationToken cancellationToken = default)
    {
        var variant = await _dbContext.ProductVariants.FirstOrDefaultAsync(
            item => item.VariantSku == variantSku,
            cancellationToken);
        if (variant == null)
        {
            return;
        }

        variant.AvailableStock = ComputeAvailable(variant);
        await _dbContext.SaveChangesAsync(cancellationToken);
        var expectedInventoryVersion = variant.InventoryVersion;

        var mappings = await _dbContext.TenantMarketplaceListingMaps
            .Where(item => item.SabrVariantSku == variantSku)
            .ToListAsync(cancellationToken);
        if (!_features.GlobalInventoryWrite)
        {
            var pilots = _features.InventoryPilotSellerIds.ToHashSet();
            mappings = mappings.Where(item => pilots.Contains(item.SellerId)).ToList();
            _logger.LogInformation(
                "INVENTORY_OBSERVATION_MODE sku={Sku} version={Version} available={Available} pilotMappings={PilotMappings}",
                variantSku, expectedInventoryVersion, variant.AvailableStock, mappings.Count);
        }
        if (mappings.Count == 0)
        {
            return;
        }

        foreach (var mapping in mappings)
        {
            var listingIdentity = !string.IsNullOrWhiteSpace(mapping.UserProductId)
                ? $"up:{mapping.UserProductId}"
                : $"item:{mapping.MlItemId}:{mapping.MlVariationId ?? "root"}";
            var dedupeKey = $"stock:{mapping.Provider}:{mapping.IntegrationId}:{listingIdentity}:v{expectedInventoryVersion}";
            var now = DateTimeOffset.UtcNow;
            var payload = JsonSerializer.Serialize(new StockSyncJobPayload(mapping.Id, variantSku, expectedInventoryVersion, variant.AvailableStock));
            const string pendingStatus = "PENDING";
            const string emptyJson = "{}";
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO marketplace_operation_jobs
                    (id, tenant_id, client_id, provider, operation_type, dedupe_key, status,
                     payload_json, result_json, total, processed, succeeded, failed, attempts,
                     inventory_version, created_at, updated_at)
                VALUES
                    ({Guid.NewGuid()}, {mapping.TenantId}, {mapping.ClientId}, {(int)mapping.Provider},
                     {MarketplaceOperationJobService.SyncStock}, {dedupeKey}, {pendingStatus},
                     CAST({payload} AS jsonb), CAST({emptyJson} AS jsonb), 1, 0, 0, 0, 0,
                     {expectedInventoryVersion}, {now}, {now})
                ON CONFLICT (dedupe_key) WHERE dedupe_key IS NOT NULL DO NOTHING", cancellationToken);
        }
    }

    public async Task<string> ProcessStockJobAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<StockSyncJobPayload>(payloadJson)
            ?? throw new InvalidOperationException("STOCK_JOB_PAYLOAD_INVALID");
        var mapping = await _dbContext.TenantMarketplaceListingMaps.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == payload.MappingId, cancellationToken);
        if (mapping == null) return "SUPERSEDED";
        if (!_features.GlobalInventoryWrite && !_features.InventoryPilotSellerIds.Contains(mapping.SellerId)) return "SUPERSEDED";

        var current = await _dbContext.ProductVariants.AsNoTracking()
            .Where(item => item.VariantSku == payload.VariantSku)
            .Select(item => new { item.InventoryVersion, Available = Math.Max(0, item.PhysicalStock - item.ReservedStock - item.SafetyBuffer) })
            .SingleOrDefaultAsync(cancellationToken);
        if (current == null || current.InventoryVersion != payload.InventoryVersion) return "SUPERSEDED";

        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == mapping.TenantId
                    && item.ClientId == mapping.ClientId
                    && item.Provider == mapping.Provider
                    && (mapping.IntegrationId.HasValue ? item.Id == mapping.IntegrationId.Value : item.SellerId == mapping.SellerId),
            cancellationToken);
        if (connection == null) throw new InvalidOperationException("STOCK_JOB_CONNECTION_NOT_FOUND");
        var token = await _oauthService.GetValidAccessTokenAsync(connection, cancellationToken);

        var versionBeforeWrite = await _dbContext.ProductVariants.AsNoTracking()
            .Where(item => item.VariantSku == payload.VariantSku)
            .Select(item => item.InventoryVersion)
            .SingleAsync(cancellationToken);
        if (versionBeforeWrite != payload.InventoryVersion) return "SUPERSEDED";

        if (!string.IsNullOrWhiteSpace(mapping.UserProductId))
        {
            var stock = await _mercadoLivreApiClient.GetUserProductStockAsync(mapping.UserProductId, token, cancellationToken);
            var warehouses = stock.Locations
                .Where(location => string.Equals(location.Type, "seller_warehouse", StringComparison.OrdinalIgnoreCase)
                                   && !string.IsNullOrWhiteSpace(location.StoreId))
                .ToList();
            if (warehouses.Count > 0)
            {
                var versionBeforeUserProductWrite = await _dbContext.ProductVariants.AsNoTracking()
                    .Where(item => item.VariantSku == payload.VariantSku)
                    .Select(item => item.InventoryVersion)
                    .SingleAsync(cancellationToken);
                if (versionBeforeUserProductWrite != payload.InventoryVersion) return "SUPERSEDED";

                var allocation = AllocateWarehouseStock(warehouses, current.Available);
                await _mercadoLivreApiClient.UpdateUserProductWarehouseStockAsync(
                    mapping.UserProductId,
                    stock.Version,
                    allocation,
                    token,
                    cancellationToken);
                return "COMPLETED";
            }

            if (stock.Locations.Any(location => string.Equals(location.Type, "meli_facility", StringComparison.OrdinalIgnoreCase))
                && stock.Locations.All(location => string.Equals(location.Type, "meli_facility", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogInformation(
                    "INVENTORY_EXTERNALLY_MANAGED mapping={MappingId} userProduct={UserProductId} sku={Sku}",
                    mapping.Id, mapping.UserProductId, payload.VariantSku);
                return "COMPLETED";
            }
        }

        if (string.IsNullOrWhiteSpace(mapping.MlVariationId))
            await _mercadoLivreApiClient.UpdateItemStockAsync(mapping.MlItemId, current.Available, token, cancellationToken);
        else
            await _mercadoLivreApiClient.UpdateVariationStockAsync(mapping.MlItemId, mapping.MlVariationId, current.Available, token, cancellationToken);
        return "COMPLETED";
    }

    public static IReadOnlyCollection<MercadoLivreUserProductStockLocation> AllocateWarehouseStock(
        IReadOnlyCollection<MercadoLivreUserProductStockLocation> source,
        int requestedAvailable)
    {
        var locations = source
            .Where(location => !string.IsNullOrWhiteSpace(location.StoreId))
            .OrderBy(location => location.StoreId, StringComparer.Ordinal)
            .Select(location => new MercadoLivreUserProductStockLocation
            {
                Type = "seller_warehouse",
                StoreId = location.StoreId,
                NetworkNodeId = location.NetworkNodeId,
                Quantity = Math.Max(0, location.Quantity)
            })
            .ToList();
        if (locations.Count == 0) return locations;

        var target = Math.Max(0, requestedAvailable);
        var currentTotal = locations.Sum(location => location.Quantity);
        if (target == 0)
        {
            locations.ForEach(location => location.Quantity = 0);
            return locations;
        }
        if (currentTotal == 0)
        {
            locations[0].Quantity = target;
            return locations;
        }

        var assigned = 0;
        var shares = locations.Select(location =>
        {
            var exact = (decimal)target * location.Quantity / currentTotal;
            var floor = (int)Math.Floor(exact);
            location.Quantity = floor;
            assigned += floor;
            return new { Location = location, Fraction = exact - floor };
        }).OrderByDescending(item => item.Fraction).ThenBy(item => item.Location.StoreId, StringComparer.Ordinal).ToList();
        for (var index = 0; index < target - assigned; index++)
        {
            shares[index % shares.Count].Location.Quantity++;
        }
        return locations;
    }

    public sealed record StockSyncJobPayload(Guid MappingId, string VariantSku, long InventoryVersion, int RequestedAvailable);
}
