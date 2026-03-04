using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Options;
using Sabr.Application.Validation;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;

namespace Sabr.Application.Services;

public sealed class MercadoLivrePublishService
{
    private readonly IAppDbContext _dbContext;
    private readonly MercadoLivreOAuthService _oauthService;
    private readonly MercadoLivrePublishValidationService _validationService;
    private readonly IMercadoLivreApiClient _mercadoLivreApiClient;
    private readonly MercadoLivreSyncService _syncService;
    private readonly StockAvailabilityService _stockAvailabilityService;
    private readonly MercadoLivreOptions _options;

    public MercadoLivrePublishService(
        IAppDbContext dbContext,
        MercadoLivreOAuthService oauthService,
        MercadoLivrePublishValidationService validationService,
        IMercadoLivreApiClient mercadoLivreApiClient,
        MercadoLivreSyncService syncService,
        StockAvailabilityService stockAvailabilityService,
        IOptions<MercadoLivreOptions> options)
    {
        _dbContext = dbContext;
        _oauthService = oauthService;
        _validationService = validationService;
        _mercadoLivreApiClient = mercadoLivreApiClient;
        _syncService = syncService;
        _stockAvailabilityService = stockAvailabilityService;
        _options = options.Value;
    }

    public async Task<ServiceResult<MercadoLivrePublishResult>> PublishAsync(
        string tenantId,
        Guid clientId,
        MercadoLivrePublishRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty)
        {
            return ServiceResult<MercadoLivrePublishResult>.Failure(new[]
            {
                new ValidationError("context", "Invalid tenant/client context")
            });
        }

        if (!_options.Features.Publish)
        {
            return ServiceResult<MercadoLivrePublishResult>.Failure(new[]
            {
                new ValidationError("feature", "ML_PUBLISH_DISABLED")
            });
        }

        if (!MercadoLivreSellerIdParser.TryParseOptional(request.SellerId, out var normalizedSeller))
        {
            return ServiceResult<MercadoLivrePublishResult>.Failure(new[]
            {
                new ValidationError("sellerId", "SellerId must be numeric")
            });
        }

        var connectionsQuery = _dbContext.TenantMarketplaceConnections
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == MarketplaceProvider.MercadoLivre);
        if (normalizedSeller.HasValue)
        {
            connectionsQuery = connectionsQuery.Where(item => item.SellerId == normalizedSeller.Value);
        }

        var connections = await connectionsQuery
            .OrderBy(item => item.SellerId)
            .ToListAsync(cancellationToken);
        if (connections.Count == 0)
        {
            return ServiceResult<MercadoLivrePublishResult>.Failure(new[]
            {
                new ValidationError("sellerId", "No active Mercado Livre connection found")
            });
        }

        var validation = await _validationService.ValidateAsync(
            tenantId,
            clientId,
            new MercadoLivrePublishValidateRequest
            {
                CatalogId = request.CatalogId,
                PlanId = request.PlanId,
                SabrVariantSkus = request.SabrVariantSkus
            },
            cancellationToken);
        if (!validation.Succeeded || validation.Data == null)
        {
            return ServiceResult<MercadoLivrePublishResult>.Failure(validation.Errors);
        }

        var validationBySku = validation.Data.Items.ToDictionary(
            item => item.SabrVariantSku,
            item => item,
            StringComparer.Ordinal);
        var allSkus = validationBySku.Keys.ToList();
        var eligibleSkus = validation.Data.Items
            .Where(item => item.Eligible)
            .Select(item => item.SabrVariantSku)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var variants = await _dbContext.ProductVariants
            .AsNoTracking()
            .Where(item => eligibleSkus.Contains(item.VariantSku))
            .ToListAsync(cancellationToken);
        var variantBySku = variants.ToDictionary(item => item.VariantSku, StringComparer.Ordinal);

        var baseSkus = variants.Select(item => item.BaseSku).Distinct(StringComparer.Ordinal).ToList();
        var products = await _dbContext.Products
            .AsNoTracking()
            .Where(item => baseSkus.Contains(item.Sku))
            .ToListAsync(cancellationToken);
        var productBySku = products.ToDictionary(item => item.Sku, StringComparer.Ordinal);

        var images = await _dbContext.ProductImages
            .AsNoTracking()
            .Where(item => baseSkus.Contains(item.ProductSku))
            .OrderBy(item => item.SortOrder)
            .ToListAsync(cancellationToken);
        var imageUrlsByBaseSku = images
            .GroupBy(item => item.ProductSku)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item => item.IsPrimary)
                    .ThenBy(item => item.SortOrder)
                    .Select(item => item.Url)
                    .Where(url => !string.IsNullOrWhiteSpace(url))
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);

        var connectionIds = connections.Select(item => item.Id).Distinct().ToList();
        var existingMappings = await _dbContext.TenantMarketplaceListingMaps
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == MarketplaceProvider.MercadoLivre
                           && item.IntegrationId.HasValue
                           && connectionIds.Contains(item.IntegrationId.Value)
                           && allSkus.Contains(item.SabrVariantSku))
            .ToListAsync(cancellationToken);
        var mappingBySellerSku = existingMappings
            .GroupBy(item => BuildIntegrationSkuKey(item.IntegrationId, item.SabrVariantSku), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var result = new MercadoLivrePublishResult();
        var changedSkus = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connection in connections)
        {
            var accessToken = await _oauthService.GetValidAccessTokenAsync(connection, cancellationToken);
            foreach (var sku in allSkus)
            {
                var validationItem = validationBySku[sku];
                if (!validationItem.Eligible)
                {
                    result.Items.Add(new MercadoLivrePublishItemResult
                    {
                        SellerId = MercadoLivreSellerIdParser.ToApiString(connection.SellerId),
                        SabrVariantSku = sku,
                        Status = "SKIPPED",
                        Reasons = validationItem.Reasons.ToList(),
                        Message = "SKU com pendencias de publicacao"
                    });
                    continue;
                }

                var key = BuildIntegrationSkuKey(connection.Id, sku);
                if (mappingBySellerSku.TryGetValue(key, out var existingMap))
                {
                    result.Items.Add(new MercadoLivrePublishItemResult
                    {
                        SellerId = MercadoLivreSellerIdParser.ToApiString(connection.SellerId),
                        SabrVariantSku = sku,
                        Status = "ALREADY_MAPPED",
                        MlItemId = existingMap.MlItemId,
                        MlVariationId = existingMap.MlVariationId,
                        Message = "SKU ja publicado para este seller"
                    });
                    changedSkus.Add(sku);
                    continue;
                }

                if (!variantBySku.TryGetValue(sku, out var variant)
                    || !productBySku.TryGetValue(variant.BaseSku, out var product))
                {
                    result.Items.Add(new MercadoLivrePublishItemResult
                    {
                        SellerId = MercadoLivreSellerIdParser.ToApiString(connection.SellerId),
                        SabrVariantSku = sku,
                        Status = "FAILED",
                        Message = "Dados de produto/variante nao encontrados"
                    });
                    continue;
                }

                if (!imageUrlsByBaseSku.TryGetValue(product.Sku, out var pictureUrls) || pictureUrls.Count == 0)
                {
                    result.Items.Add(new MercadoLivrePublishItemResult
                    {
                        SellerId = MercadoLivreSellerIdParser.ToApiString(connection.SellerId),
                        SabrVariantSku = sku,
                        Status = "FAILED",
                        Message = "Produto sem imagens publicaveis"
                    });
                    continue;
                }

                try
                {
                    var created = await _mercadoLivreApiClient.CreateItemAsync(
                        BuildCreateItemRequest(product, variant, pictureUrls),
                        accessToken,
                        cancellationToken);

                    var newMap = new TenantMarketplaceListingMap
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantId,
                        ClientId = clientId,
                        Provider = MarketplaceProvider.MercadoLivre,
                        IntegrationId = connection.Id,
                        SellerId = connection.SellerId,
                        MlItemId = created.ItemId,
                        MlVariationId = created.VariationId,
                        SabrVariantSku = sku,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    _dbContext.TenantMarketplaceListingMaps.Add(newMap);
                    mappingBySellerSku[key] = newMap;

                    result.Items.Add(new MercadoLivrePublishItemResult
                    {
                        SellerId = MercadoLivreSellerIdParser.ToApiString(connection.SellerId),
                        SabrVariantSku = sku,
                        Status = "PUBLISHED",
                        MlItemId = created.ItemId,
                        MlVariationId = created.VariationId,
                        Message = "Publicacao criada com sucesso"
                    });
                    changedSkus.Add(sku);
                }
                catch (Exception ex)
                {
                    result.Items.Add(new MercadoLivrePublishItemResult
                    {
                        SellerId = MercadoLivreSellerIdParser.ToApiString(connection.SellerId),
                        SabrVariantSku = sku,
                        Status = "FAILED",
                        Message = ex.Message
                    });
                }
            }
        }

        result.Total = result.Items.Count;
        result.Published = result.Items.Count(item => item.Status == "PUBLISHED");
        result.AlreadyMapped = result.Items.Count(item => item.Status == "ALREADY_MAPPED");
        result.Skipped = result.Items.Count(item => item.Status == "SKIPPED");
        result.Failed = result.Items.Count(item => item.Status == "FAILED");

        await _dbContext.SaveChangesAsync(cancellationToken);
        if (changedSkus.Count > 0)
        {
            await _stockAvailabilityService.SyncStockForSkusAsync(tenantId, clientId, changedSkus, cancellationToken);
        }

        return ServiceResult<MercadoLivrePublishResult>.Success(result);
    }

    public async Task<ServiceResult<MercadoLivreListListingsResult>> ListListingsAsync(
        string tenantId,
        Guid clientId,
        string? sellerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty)
        {
            return ServiceResult<MercadoLivreListListingsResult>.Failure(new[]
            {
                new ValidationError("context", "Invalid tenant/client context")
            });
        }

        if (!MercadoLivreSellerIdParser.TryParseOptional(sellerId, out var normalizedSeller))
        {
            return ServiceResult<MercadoLivreListListingsResult>.Failure(new[]
            {
                new ValidationError("sellerId", "SellerId must be numeric")
            });
        }

        var query = _dbContext.TenantMarketplaceListingMaps
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == MarketplaceProvider.MercadoLivre);
        if (normalizedSeller.HasValue)
        {
            query = query.Where(item => item.SellerId == normalizedSeller.Value);
        }

        var mappings = await query
            .OrderBy(item => item.SellerId)
            .ThenBy(item => item.SabrVariantSku)
            .ToListAsync(cancellationToken);
        var variantSkus = mappings.Select(item => item.SabrVariantSku).Distinct(StringComparer.Ordinal).ToList();
        var variants = await _dbContext.ProductVariants
            .AsNoTracking()
            .Where(item => variantSkus.Contains(item.VariantSku))
            .ToListAsync(cancellationToken);
        var variantBySku = variants.ToDictionary(item => item.VariantSku, StringComparer.Ordinal);

        var baseSkus = variants.Select(item => item.BaseSku).Distinct(StringComparer.Ordinal).ToList();
        var products = await _dbContext.Products
            .AsNoTracking()
            .Where(item => baseSkus.Contains(item.Sku))
            .ToListAsync(cancellationToken);
        var productBySku = products.ToDictionary(item => item.Sku, StringComparer.Ordinal);

        var items = new List<MercadoLivreListingItemResult>(mappings.Count);
        foreach (var mapping in mappings)
        {
            variantBySku.TryGetValue(mapping.SabrVariantSku, out var variant);
            Domain.Entities.Product? product = null;
            if (variant != null)
            {
                productBySku.TryGetValue(variant.BaseSku, out product);
            }

            items.Add(new MercadoLivreListingItemResult
            {
                Id = mapping.Id,
                IntegrationId = mapping.IntegrationId,
                SellerId = MercadoLivreSellerIdParser.ToApiString(mapping.SellerId),
                MlItemId = mapping.MlItemId,
                MlVariationId = mapping.MlVariationId,
                SabrVariantSku = mapping.SabrVariantSku,
                ProductName = product?.Name ?? variant?.Name,
                CatalogPriceCents = variant?.CatalogPriceCents ?? 0,
                PhysicalStock = variant?.PhysicalStock ?? 0,
                ReservedStock = variant?.ReservedStock ?? 0,
                AvailableStock = variant?.AvailableStock ?? 0,
                UpdatedAt = mapping.UpdatedAt
            });
        }

        return ServiceResult<MercadoLivreListListingsResult>.Success(new MercadoLivreListListingsResult
        {
            Total = items.Count,
            Items = items
        });
    }

    public Task<ServiceResult<MercadoLivreSyncNowResult>> ReconcileAsync(
        string tenantId,
        Guid clientId,
        string? sellerId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Features.Reconcile)
        {
            return Task.FromResult(ServiceResult<MercadoLivreSyncNowResult>.Failure(new[]
            {
                new ValidationError("feature", "ML_RECONCILE_DISABLED")
            }));
        }

        // Reconcile usa o mesmo fluxo de sync idempotente no MVP+:
        // reimporta a janela configurada para reparar drift.
        return _syncService.SyncNowAsync(tenantId, clientId, sellerId, cancellationToken);
    }

    private static string BuildIntegrationSkuKey(Guid? integrationId, string sku)
    {
        return $"{integrationId.GetValueOrDefault()}|{sku}";
    }

    private static MercadoLivreCreateItemRequest BuildCreateItemRequest(
        Domain.Entities.Product product,
        ProductVariant variant,
        IReadOnlyCollection<string> pictureUrls)
    {
        var title = string.IsNullOrWhiteSpace(variant.Name)
            ? product.Name
            : $"{product.Name} - {variant.Name}";
        var rawDescription = string.IsNullOrWhiteSpace(product.Description)
            ? product.Name
            : product.Description;

        return new MercadoLivreCreateItemRequest
        {
            Title = title.Trim(),
            CategoryId = string.IsNullOrWhiteSpace(product.CategoryId) ? null : product.CategoryId.Trim(),
            Price = Math.Round(Math.Max(0.01m, variant.CatalogPriceCents / 100m), 2),
            AvailableQuantity = StockAvailabilityService.ComputeAvailable(variant),
            Description = rawDescription?.Trim(),
            PictureUrls = pictureUrls.ToList(),
            SellerCustomField = variant.VariantSku,
            SabrVariantSku = variant.VariantSku
        };
    }
}
