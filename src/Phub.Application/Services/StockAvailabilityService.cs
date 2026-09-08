using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phub.Application.Abstractions;
using Phub.Application.Options;
using Phub.Domain.Entities;

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

        var sellerIds = mappings
            .Select(item => item.SellerId)
            .Distinct()
            .ToList();
        var integrationIds = mappings
            .Where(item => item.IntegrationId.HasValue)
            .Select(item => item.IntegrationId!.Value)
            .Distinct()
            .ToList();
        var connections = await _dbContext.TenantMarketplaceConnections
            .Where(item => integrationIds.Contains(item.Id) || sellerIds.Contains(item.SellerId))
            .ToListAsync(cancellationToken);

        var connectionBySeller = connections
            .GroupBy(item => (item.TenantId, item.ClientId, item.SellerId))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.UpdatedAt).First());
        var connectionByIntegration = connections.ToDictionary(item => item.Id);
        foreach (var mapping in mappings)
        {
            TenantMarketplaceConnection? connection = null;
            if (mapping.IntegrationId.HasValue)
            {
                connectionByIntegration.TryGetValue(mapping.IntegrationId.Value, out connection);
            }

            if (connection == null && !connectionBySeller.TryGetValue((mapping.TenantId, mapping.ClientId, mapping.SellerId), out connection))
            {
                continue;
            }

            try
            {
                var currentVersion = await _dbContext.ProductVariants
                    .AsNoTracking()
                    .Where(item => item.VariantSku == variantSku)
                    .Select(item => item.InventoryVersion)
                    .SingleAsync(cancellationToken);
                if (currentVersion != expectedInventoryVersion)
                {
                    _logger.LogInformation(
                        "ML_SYNC_SUPERSEDED sku={Sku} expectedVersion={ExpectedVersion} currentVersion={CurrentVersion}",
                        variantSku,
                        expectedInventoryVersion,
                        currentVersion);
                    return;
                }

                var accessToken = await _oauthService.GetValidAccessTokenAsync(connection, cancellationToken);

                currentVersion = await _dbContext.ProductVariants
                    .AsNoTracking()
                    .Where(item => item.VariantSku == variantSku)
                    .Select(item => item.InventoryVersion)
                    .SingleAsync(cancellationToken);
                if (currentVersion != expectedInventoryVersion)
                {
                    _logger.LogInformation(
                        "ML_SYNC_SUPERSEDED sku={Sku} expectedVersion={ExpectedVersion} currentVersion={CurrentVersion}",
                        variantSku,
                        expectedInventoryVersion,
                        currentVersion);
                    return;
                }

                if (string.IsNullOrWhiteSpace(mapping.MlVariationId))
                {
                    await _mercadoLivreApiClient.UpdateItemStockAsync(
                        mapping.MlItemId,
                        variant.AvailableStock,
                        accessToken,
                        cancellationToken);
                }
                else
                {
                    await _mercadoLivreApiClient.UpdateVariationStockAsync(
                        mapping.MlItemId,
                        mapping.MlVariationId,
                        variant.AvailableStock,
                        accessToken,
                        cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "ML_SYNC_FAILED tenant={TenantId} client={ClientId} sku={Sku} seller={SellerId} item={ItemId}",
                    mapping.TenantId,
                    mapping.ClientId,
                    variantSku,
                    mapping.SellerId,
                    mapping.MlItemId);
            }
        }
    }
}
