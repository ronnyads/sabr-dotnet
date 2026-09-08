using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Domain.ValueObjects;

namespace Phub.Application.Services;

public sealed class MercadoLivreCatalogImportService
{
    private readonly IAppDbContext _dbContext;
    private readonly IMercadoLivreApiClient _apiClient;
    private readonly MercadoLivreOAuthService _oauthService;

    public MercadoLivreCatalogImportService(
        IAppDbContext dbContext,
        IMercadoLivreApiClient apiClient,
        MercadoLivreOAuthService oauthService)
    {
        _dbContext = dbContext;
        _apiClient = apiClient;
        _oauthService = oauthService;
    }

    public async Task<ServiceResult<MercadoLivreCatalogImportResult>> ImportAsync(
        string tenantSlug,
        Guid clientId,
        MercadoLivreCatalogImportRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        if (request.PhysicalStock < 0 || request.PhysicalStock > 1_000_000)
            return ServiceResult<MercadoLivreCatalogImportResult>.Failure([new ValidationError("physicalStock", "Stock must be between 0 and 1000000")]);

        var tenant = await _dbContext.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Slug == tenantSlug.Trim().ToLowerInvariant(), cancellationToken);
        if (tenant == null)
            return ServiceResult<MercadoLivreCatalogImportResult>.Failure([new ValidationError("tenantSlug", "Tenant not found")]);

        var connection = await _dbContext.TenantMarketplaceConnections
            .Where(x => x.TenantId == tenant.Id && x.ClientId == clientId && x.Provider == MarketplaceProvider.MercadoLivre)
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (connection == null)
            return ServiceResult<MercadoLivreCatalogImportResult>.Failure([new ValidationError("clientId", "Mercado Livre connection not found")]);

        var accessToken = await _oauthService.GetValidAccessTokenAsync(connection, cancellationToken);
        var listings = await _apiClient.SearchSellerItemsAsync(connection.SellerId.ToString(CultureInfo.InvariantCulture), request.Query, accessToken, cancellationToken);
        var brands = request.Brands.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Normalize).ToHashSet();
        var requestedItemIds = request.ItemIds.Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = listings
            .Where(x => brands.Count == 0 || brands.Contains(Normalize(ResolveBrand(x))))
            .Where(x => requestedItemIds.Count == 0 || requestedItemIds.Contains(x.ItemId))
            .ToList();
        var result = new MercadoLivreCatalogImportResult { ListingsFound = listings.Count };

        var catalogIds = await (
            from subscription in _dbContext.ClientPlanSubscriptions.AsNoTracking()
            join planCatalog in _dbContext.PlanCatalogs.AsNoTracking() on subscription.PlanId equals planCatalog.PlanId
            join catalog in _dbContext.Catalogs.AsNoTracking() on planCatalog.CatalogId equals catalog.Id
            where subscription.TenantId == tenant.Id && subscription.ClientId == clientId && subscription.IsActive && catalog.IsActive
            select catalog.Id).Distinct().ToListAsync(cancellationToken);
        if (catalogIds.Count == 0 && !request.PreviewOnly)
            return ServiceResult<MercadoLivreCatalogImportResult>.Failure([new ValidationError("catalog", "Client has no active catalog")]);

        var processedSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var listing in selected)
        {
            var skuEntries = ResolveSkus(listing).ToList();
            if (skuEntries.Count == 0)
            {
                result.Warnings.Add($"{listing.ItemId}: anúncio sem SKU válido ({listing.Title}).");
                result.Items.Add(ToResult(listing, null, "skipped_missing_sku"));
                continue;
            }

            foreach (var (sku, variationId) in skuEntries)
            {
                result.ProductsMatched++;
                var itemResult = ToResult(listing, sku, request.PreviewOnly ? "preview" : "mapped");
                result.Items.Add(itemResult);
                if (request.PreviewOnly) continue;

                if (processedSkus.Add(sku))
                {
                    var product = await _dbContext.Products.FirstOrDefaultAsync(x => x.Sku == sku, cancellationToken);
                    if (product == null)
                    {
                        product = new Product
                        {
                            Sku = sku,
                            Name = listing.Title.Trim(),
                            Brand = ResolveBrand(listing),
                            Ean = NormalizeEan(listing.Ean),
                            Description = $"Importado do anúncio {listing.ItemId} do Mercado Livre.",
                            CategoryId = ProductAdminService.UncategorizedSlug,
                            ThumbnailUrl = listing.ThumbnailUrl,
                            CostPriceCents = 0,
                            CatalogPriceCents = itemResult.CatalogPriceCents,
                            IsActive = true
                        };
                        _dbContext.Products.Add(product);
                        result.ProductsCreated++;
                        itemResult.Action = "created";
                    }
                    else
                    {
                        result.ProductsUpdated++;
                        itemResult.Action = "updated_stock";
                    }

                    var variant = await _dbContext.ProductVariants.FirstOrDefaultAsync(x => x.VariantSku == sku, cancellationToken);
                    if (variant == null)
                    {
                        variant = new ProductVariant
                        {
                            VariantSku = sku,
                            BaseSku = product.Sku,
                            Name = product.Name,
                            CostPriceCents = product.CostPriceCents,
                            CatalogPriceCents = product.CatalogPriceCents,
                            PhysicalStock = request.PhysicalStock,
                            ReservedStock = 0,
                            AvailableStock = request.PhysicalStock,
                            IsActive = true
                        };
                        _dbContext.ProductVariants.Add(variant);
                    }
                    else
                    {
                        variant.PhysicalStock = request.PhysicalStock;
                        variant.AvailableStock = Math.Max(0, request.PhysicalStock - variant.ReservedStock);
                        variant.IsActive = true;
                        variant.UpdatedAt = DateTimeOffset.UtcNow;
                    }

                    foreach (var catalogId in catalogIds)
                    {
                        if (!await _dbContext.ProductCatalogs.AnyAsync(x => x.CatalogId == catalogId && x.ProductSku == sku, cancellationToken))
                            _dbContext.ProductCatalogs.Add(new ProductCatalog { CatalogId = catalogId, ProductSku = sku });
                    }
                }

                var mapping = await _dbContext.TenantMarketplaceListingMaps.FirstOrDefaultAsync(x =>
                    x.TenantId == tenant.Id && x.ClientId == clientId && x.Provider == MarketplaceProvider.MercadoLivre &&
                    x.IntegrationId == connection.Id && x.MlItemId == listing.ItemId && x.MlVariationId == variationId, cancellationToken);
                if (mapping == null)
                {
                    _dbContext.TenantMarketplaceListingMaps.Add(new TenantMarketplaceListingMap
                    {
                        TenantId = tenant.Id,
                        ClientId = clientId,
                        Provider = MarketplaceProvider.MercadoLivre,
                        IntegrationId = connection.Id,
                        SellerId = connection.SellerId,
                        MlItemId = listing.ItemId,
                        MlVariationId = variationId,
                        SabrVariantSku = sku
                    });
                    result.MappingsCreated++;
                }
                else
                {
                    mapping.SabrVariantSku = sku;
                    mapping.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
        }

        if (!request.PreviewOnly)
        {
            _dbContext.AuditEvents.Add(new AuditEvent
            {
                TenantId = tenant.Id,
                ActorType = "AdminUser",
                ActorId = actorId,
                Action = "AdminProducts.ImportFromMercadoLivre",
                Entity = nameof(Product),
                RequestId = Guid.NewGuid(),
                MetadataJson = JsonSerializer.Serialize(new { connection.SellerId, request.Query, request.Brands, request.PhysicalStock, result.ProductsCreated, result.MappingsCreated })
            });
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return ServiceResult<MercadoLivreCatalogImportResult>.Success(result);
    }

    private static IEnumerable<(string Sku, string? VariationId)> ResolveSkus(MercadoLivreSellerItemDetails item)
    {
        if (item.Variations.Count > 0)
        {
            foreach (var variation in item.Variations)
                if (Sku.TryParse(variation.SellerSku, out var parsed)) yield return (parsed.Value, variation.VariationId);
            yield break;
        }
        if (Sku.TryParse(item.SellerSku, out var itemSku)) yield return (itemSku.Value, null);
    }

    private static MercadoLivreCatalogImportItemResult ToResult(MercadoLivreSellerItemDetails item, string? sku, string action) => new()
    {
        ItemId = item.ItemId,
        Title = item.Title,
        Sku = sku,
        Brand = ResolveBrand(item),
        ThumbnailUrl = item.ThumbnailUrl,
        CatalogPriceCents = checked((long)Math.Round(item.Price * 100m, MidpointRounding.AwayFromZero)),
        Action = action
    };

    private static string ResolveBrand(MercadoLivreSellerItemDetails item)
    {
        if (!string.IsNullOrWhiteSpace(item.Brand)) return item.Brand.Trim();
        var title = Normalize(item.Title);
        if (title.Contains("boca rosa")) return "Boca Rosa";
        if (title.Contains("principia")) return "Principia";
        return string.Empty;
    }
    private static string Normalize(string value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant().Normalize(NormalizationForm.FormC);
    }
    private static string? NormalizeEan(string? value) => !string.IsNullOrWhiteSpace(value) && value.All(char.IsDigit) && value.Length is 8 or 12 or 13 or 14 ? value : null;
}
