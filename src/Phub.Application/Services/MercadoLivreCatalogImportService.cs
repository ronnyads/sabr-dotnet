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
        if (request.CatalogPriceCents is < 0 or > 100_000_000_000)
            return ServiceResult<MercadoLivreCatalogImportResult>.Failure([new ValidationError("catalogPriceCents", "Catalog price must be between 0 and 100000000000 cents")]);

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
        // ItemIds vazio é o sinal de "nenhuma seleção explícita ainda" e só é seguro em
        // PreviewOnly (onde nada é gravado). Fora do preview, deixar passar tudo por
        // brands sozinho gravaria produtos/mapeamentos para anúncios que o usuário nunca
        // selecionou na tela de importação (achado 2.7 da auditoria).
        if (!request.PreviewOnly && requestedItemIds.Count == 0)
            return ServiceResult<MercadoLivreCatalogImportResult>.Failure([new ValidationError("itemIds", "Selecione ao menos um anúncio antes de importar.")]);
        var selected = listings
            .Where(x => brands.Count == 0 || brands.Contains(Normalize(ResolveBrand(x))))
            .Where(x => requestedItemIds.Count == 0 || requestedItemIds.Contains(x.ItemId))
            .ToList();
        var result = new MercadoLivreCatalogImportResult { ListingsFound = listings.Count };

        var assignments = new Dictionary<string, (string InternalSku, bool CreateNewProduct, long? CatalogPriceCents)>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in request.SkuAssignments ?? [])
        {
            if (!Sku.TryParse(assignment.InternalSku, out var parsedSku))
                return ServiceResult<MercadoLivreCatalogImportResult>.Failure([new ValidationError("skuAssignments", "SKU interno inválido.")]);
            if (string.Equals(parsedSku.Value, assignment.ItemId?.Trim(), StringComparison.OrdinalIgnoreCase))
                return ServiceResult<MercadoLivreCatalogImportResult>.Failure([new ValidationError("skuAssignments", "O ID do anúncio não pode ser usado como SKU interno.")]);
            if (IsMercadoLivreItemId(parsedSku.Value))
                return ServiceResult<MercadoLivreCatalogImportResult>.Failure([new ValidationError("skuAssignments", "Use um SKU interno próprio; códigos MLB numéricos são IDs externos de anúncio.")]);
            if (assignment.CatalogPriceCents is < 0 or > 100_000_000_000)
                return ServiceResult<MercadoLivreCatalogImportResult>.Failure([new ValidationError("skuAssignments", "Preço Catálogo inválido.")]);
            var key = AssignmentKey(assignment.ItemId, assignment.VariationId);
            if (string.IsNullOrWhiteSpace(assignment.ItemId) || !assignments.TryAdd(key, (parsedSku.Value, assignment.CreateNewProduct, assignment.CatalogPriceCents)))
                return ServiceResult<MercadoLivreCatalogImportResult>.Failure([new ValidationError("skuAssignments", "Anúncio/variação duplicado ou inválido.")]);
        }

        var catalogIds = await _dbContext.Catalogs
            .AsNoTracking()
            .Where(catalog => catalog.IsActive && catalog.AccessMode == CatalogAccessMode.Public)
            .Select(catalog => catalog.Id)
            .ToListAsync(cancellationToken);
        if (catalogIds.Count == 0 && !request.PreviewOnly)
        {
            var publicCatalog = new Catalog
            {
                Name = "Catálogo Público",
                Description = "Produtos disponíveis para todos os clientes aprovados.",
                AccessMode = CatalogAccessMode.Public,
                IsActive = true
            };
            _dbContext.Catalogs.Add(publicCatalog);
            catalogIds.Add(publicCatalog.Id);
        }

        var processedSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var listing in selected)
        {
            foreach (var (channelSku, variationId) in ResolveChannelIdentities(listing))
            {
                assignments.TryGetValue(AssignmentKey(listing.ItemId, variationId), out var assignment);
                var assignedSku = assignment.InternalSku;
                var sku = assignedSku ?? channelSku;
                var itemResult = ToResult(listing, channelSku, variationId, assignedSku, request.PreviewOnly ? "preview" : "mapped");
                var chosenCatalogPriceCents = assignment.CatalogPriceCents ?? request.CatalogPriceCents;
                if (chosenCatalogPriceCents.HasValue)
                    itemResult.CatalogPriceCents = chosenCatalogPriceCents.Value;
                result.Items.Add(itemResult);
                if (request.PreviewOnly) continue;

                // A channel SKU is only an automatic bridge when it already identifies a
                // unique internal variant. Never create a master SKU from a marketplace ID.
                if (sku == null)
                {
                    itemResult.Action = "skipped_missing_internal_sku";
                    result.Warnings.Add($"{listing.ItemId}: informe o SKU interno para vincular este anúncio.");
                    continue;
                }
                if (IsMercadoLivreItemId(sku))
                {
                    itemResult.Action = "skipped_external_id_as_internal_sku";
                    result.Warnings.Add($"{listing.ItemId}: o código {sku} parece um Item ID do ML. Informe um SKU interno próprio.");
                    continue;
                }

                var existingVariant = await _dbContext.ProductVariants.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.VariantSku == sku, cancellationToken);
                var productSku = existingVariant?.BaseSku ?? sku;
                var existingProduct = await _dbContext.Products.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Sku == productSku, cancellationToken);
                if ((existingVariant != null && !existingVariant.IsActive) || (existingProduct != null && !existingProduct.IsActive))
                {
                    itemResult.Action = "skipped_inactive_internal_sku";
                    result.Warnings.Add($"{listing.ItemId}: SKU interno {sku} está inativo no catálogo.");
                    continue;
                }
                if (existingProduct != null && existingVariant == null && await _dbContext.ProductVariants
                    .AnyAsync(x => x.BaseSku == sku, cancellationToken))
                {
                    itemResult.Action = "skipped_select_variant";
                    result.Warnings.Add($"{listing.ItemId}: produto {sku} tem variações; selecione o SKU interno da variação correta.");
                    continue;
                }
                if (assignedSku == null && existingVariant == null)
                {
                    itemResult.Action = "skipped_unknown_channel_sku";
                    result.Warnings.Add($"{listing.ItemId}: SKU do canal {sku} não existe no catálogo. Informe o SKU interno.");
                    continue;
                }
                if (assignedSku != null && existingProduct == null && !assignment.CreateNewProduct)
                {
                    itemResult.Action = "skipped_internal_sku_not_found";
                    result.Warnings.Add($"{listing.ItemId}: SKU interno {sku} não existe. Se deseja criá-lo, selecione Criar SKU novo.");
                    continue;
                }
                if (assignedSku != null && existingProduct != null && assignment.CreateNewProduct)
                {
                    itemResult.Action = "skipped_internal_sku_already_exists";
                    result.Warnings.Add($"{listing.ItemId}: SKU interno {sku} já existe. Se deseja vinculá-lo, selecione Vincular existente.");
                    continue;
                }
                if (existingProduct == null && chosenCatalogPriceCents is null or <= 0)
                {
                    itemResult.Action = "skipped_missing_catalog_cost";
                    result.Warnings.Add($"{listing.ItemId}: informe o Preço Catálogo (custo do seller) para criar o SKU interno {sku}.");
                    continue;
                }
                result.ProductsMatched++;
                itemResult.InternalSku = sku;

                if (processedSkus.Add(sku))
                {
                    var product = await _dbContext.Products.FirstOrDefaultAsync(x => x.Sku == productSku, cancellationToken);
                    if (product == null)
                    {
                        product = new Product
                        {
                            Sku = productSku,
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
                        // Linking an existing product must not overwrite authoritative
                        // catalogue price or central inventory.
                        result.ProductsLinkedExisting++;
                        itemResult.Action = "linked_existing";
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
                            SafetyBuffer = 2,
                            InventoryVersion = 1,
                            AvailableStock = Math.Max(0, request.PhysicalStock - 2),
                            IsActive = true
                        };
                        _dbContext.ProductVariants.Add(variant);
                    }
                    else
                    {
                        // Existing stock is owned by inventory operations, not imports.
                    }

                    foreach (var catalogId in catalogIds)
                    {
                        if (!await _dbContext.ProductCatalogs.AnyAsync(x => x.CatalogId == catalogId && x.ProductSku == productSku, cancellationToken))
                            _dbContext.ProductCatalogs.Add(new ProductCatalog { CatalogId = catalogId, ProductSku = productSku });
                    }
                }

                var mapping = await _dbContext.TenantMarketplaceListingMaps.FirstOrDefaultAsync(x =>
                    x.TenantId == tenant.Id && x.ClientId == clientId && x.Provider == MarketplaceProvider.MercadoLivre &&
                    x.SellerId == connection.SellerId && x.MlItemId == listing.ItemId && x.MlVariationId == variationId, cancellationToken);
                if (mapping == null)
                {
                    mapping = new TenantMarketplaceListingMap
                    {
                        TenantId = tenant.Id,
                        ClientId = clientId,
                        Provider = MarketplaceProvider.MercadoLivre,
                        IntegrationId = connection.Id,
                        SellerId = connection.SellerId,
                        MlItemId = listing.ItemId,
                        MlVariationId = variationId,
                        ChannelSku = channelSku,
                        SabrVariantSku = sku,
                        MappingVersion = 1
                    };
                    _dbContext.TenantMarketplaceListingMaps.Add(mapping);
                    result.MappingsCreated++;
                }
                else
                {
                    mapping.IntegrationId = connection.Id;
                    if (!string.Equals(mapping.SabrVariantSku, sku, StringComparison.Ordinal))
                    {
                        var previousSku = mapping.SabrVariantSku;
                        mapping.SabrVariantSku = sku;
                        mapping.MappingVersion++;
                        mapping.UpdatedAt = DateTimeOffset.UtcNow;
                        result.MappingsUpdated++;
                        _dbContext.AuditEvents.Add(new AuditEvent
                        {
                            TenantId = tenant.Id,
                            ActorType = "AdminUser",
                            ActorId = actorId,
                            Action = "AdminProducts.RemapMarketplaceListingFutureOrders",
                            Entity = nameof(TenantMarketplaceListingMap),
                            EntityId = mapping.Id,
                            RequestId = Guid.NewGuid(),
                            MetadataJson = JsonSerializer.Serialize(new { listing.ItemId, variationId, connection.SellerId, previousSku, newSku = sku, mapping.MappingVersion })
                        });
                    }
                    mapping.ChannelSku = channelSku;
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
                MetadataJson = JsonSerializer.Serialize(new { connection.SellerId, request.Query, request.Brands, request.PhysicalStock, request.CatalogPriceCents, request.SkuAssignments, result.ProductsCreated, result.MappingsCreated })
            });
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return ServiceResult<MercadoLivreCatalogImportResult>.Success(result);
    }

    private static IEnumerable<(string? ChannelSku, string? VariationId)> ResolveChannelIdentities(MercadoLivreSellerItemDetails item)
    {
        if (item.Variations.Count > 0)
        {
            foreach (var variation in item.Variations)
                yield return (Sku.TryParse(variation.SellerSku, out var parsed) ? parsed.Value : null, variation.VariationId);
            yield break;
        }
        yield return (Sku.TryParse(item.SellerSku, out var itemSku) ? itemSku.Value : null, null);
    }

    private static string AssignmentKey(string? itemId, string? variationId) => $"{itemId?.Trim() ?? string.Empty}|{variationId?.Trim() ?? string.Empty}";

    private static bool IsMercadoLivreItemId(string sku) =>
        sku.Length > 3 && sku.StartsWith("MLB", StringComparison.Ordinal) && sku[3..].All(char.IsDigit);

    private static MercadoLivreCatalogImportItemResult ToResult(MercadoLivreSellerItemDetails item, string? channelSku, string? variationId, string? internalSku, string action) => new()
    {
        ItemId = item.ItemId,
        Title = item.Title,
        Sku = channelSku,
        VariationId = variationId,
        InternalSku = internalSku,
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
