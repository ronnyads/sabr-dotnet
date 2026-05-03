using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phub.Application.Abstractions;
using Phub.Application.Options;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class TikTokShopPublishService
{
    private readonly IAppDbContext _dbContext;
    private readonly ITikTokShopApiClient _apiClient;
    private readonly TikTokShopOAuthService _oauthService;
    private readonly TikTokShopOptions _options;
    private readonly ILogger<TikTokShopPublishService> _logger;

    public TikTokShopPublishService(
        IAppDbContext dbContext,
        ITikTokShopApiClient apiClient,
        TikTokShopOAuthService oauthService,
        IOptions<TikTokShopOptions> options,
        ILogger<TikTokShopPublishService> logger)
    {
        _dbContext = dbContext;
        _apiClient = apiClient;
        _oauthService = oauthService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ServiceResult<TikTokShopPublishValidateResult>> ValidatePublishAsync(
        string tenantId,
        Guid clientId,
        TikTokShopPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Features.Publish)
        {
            return ServiceResult<TikTokShopPublishValidateResult>.Failure(new[]
            {
                new ValidationError("feature", "TIKTOK_PUBLISH_DISABLED")
            });
        }

        var skus = (request.SabrVariantSkus ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (skus.Count == 0)
        {
            return ServiceResult<TikTokShopPublishValidateResult>.Failure(new[]
            {
                new ValidationError("sabrVariantSkus", "Nenhum SKU informado")
            });
        }

        var variants = await _dbContext.ProductVariants
            .AsNoTracking()
            .Where(v => skus.Contains(v.VariantSku))
            .ToListAsync(cancellationToken);

        var variantBySku = variants.ToDictionary(v => v.VariantSku, StringComparer.Ordinal);
        var baseSkus = variants.Select(v => v.BaseSku).Distinct(StringComparer.Ordinal).ToList();

        var products = await _dbContext.Products
            .AsNoTracking()
            .Where(p => baseSkus.Contains(p.Sku))
            .ToListAsync(cancellationToken);
        var productBySku = products.ToDictionary(p => p.Sku, StringComparer.Ordinal);

        var images = await _dbContext.ProductImages
            .AsNoTracking()
            .Where(i => baseSkus.Contains(i.ProductSku))
            .ToListAsync(cancellationToken);
        var imagesByBaseSku = images
            .GroupBy(i => i.ProductSku, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var existingMappings = await _dbContext.TenantMarketplaceListingMaps
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId
                        && m.ClientId == clientId
                        && m.Provider == MarketplaceProvider.TikTokShop
                        && skus.Contains(m.SabrVariantSku))
            .Select(m => m.SabrVariantSku)
            .ToListAsync(cancellationToken);
        var alreadyMappedSkus = new HashSet<string>(existingMappings, StringComparer.Ordinal);

        var items = new List<TikTokShopPublishValidateItem>();

        foreach (var sku in skus)
        {
            var reasons = new List<string>();

            if (alreadyMappedSkus.Contains(sku))
            {
                reasons.Add("Já publicado no TikTok Shop");
                items.Add(new TikTokShopPublishValidateItem
                {
                    SabrVariantSku = sku,
                    Eligible = false,
                    AlreadyMapped = true,
                    Reasons = reasons
                });
                continue;
            }

            if (!variantBySku.TryGetValue(sku, out var variant))
            {
                reasons.Add("SKU não encontrado no catálogo");
                items.Add(new TikTokShopPublishValidateItem
                {
                    SabrVariantSku = sku,
                    Eligible = false,
                    Reasons = reasons
                });
                continue;
            }

            if (!productBySku.TryGetValue(variant.BaseSku, out var product))
            {
                reasons.Add("Produto base não encontrado");
                items.Add(new TikTokShopPublishValidateItem
                {
                    SabrVariantSku = sku,
                    Eligible = false,
                    Reasons = reasons
                });
                continue;
            }

            if (string.IsNullOrWhiteSpace(product.Name))
                reasons.Add("Nome do produto em falta");

            if (product.CatalogPriceCents <= 0)
                reasons.Add("Preço de catálogo não definido");

            if (!imagesByBaseSku.TryGetValue(variant.BaseSku, out var productImages) || productImages.Count == 0)
                reasons.Add("Sem imagens cadastradas");

            if (string.IsNullOrWhiteSpace(request.TikTokCategoryId))
                reasons.Add("Categoria TikTok não selecionada");

            items.Add(new TikTokShopPublishValidateItem
            {
                SabrVariantSku = sku,
                ProductName = product.Name,
                PriceCents = product.CatalogPriceCents,
                ImageCount = productImages?.Count ?? 0,
                Eligible = reasons.Count == 0,
                Reasons = reasons
            });
        }

        return ServiceResult<TikTokShopPublishValidateResult>.Success(new TikTokShopPublishValidateResult
        {
            Total = items.Count,
            Eligible = items.Count(i => i.Eligible),
            Ineligible = items.Count(i => !i.Eligible),
            Items = items
        });
    }

    public async Task<ServiceResult<TikTokShopPublishResult>> PublishAsync(
        string tenantId,
        Guid clientId,
        TikTokShopPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Features.Publish)
        {
            return ServiceResult<TikTokShopPublishResult>.Failure(new[]
            {
                new ValidationError("feature", "TIKTOK_PUBLISH_DISABLED")
            });
        }

        if (string.IsNullOrWhiteSpace(request.TikTokCategoryId))
        {
            return ServiceResult<TikTokShopPublishResult>.Failure(new[]
            {
                new ValidationError("tikTokCategoryId", "Categoria TikTok obrigatória")
            });
        }

        var tokenResult = await _oauthService.GetValidAccessTokenAsync(tenantId, clientId, cancellationToken);
        if (!tokenResult.Succeeded || tokenResult.Data == null)
        {
            return ServiceResult<TikTokShopPublishResult>.Failure(tokenResult.Errors);
        }

        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            c => c.TenantId == tenantId && c.ClientId == clientId && c.Provider == MarketplaceProvider.TikTokShop,
            cancellationToken);

        if (connection == null)
        {
            return ServiceResult<TikTokShopPublishResult>.Failure(new[]
            {
                new ValidationError("connection", "TikTok Shop não está conectado")
            });
        }

        var skus = (request.SabrVariantSkus ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var variants = await _dbContext.ProductVariants
            .AsNoTracking()
            .Where(v => skus.Contains(v.VariantSku))
            .ToListAsync(cancellationToken);
        var variantBySku = variants.ToDictionary(v => v.VariantSku, StringComparer.Ordinal);

        var baseSkus = variants.Select(v => v.BaseSku).Distinct(StringComparer.Ordinal).ToList();
        var products = await _dbContext.Products
            .AsNoTracking()
            .Where(p => baseSkus.Contains(p.Sku))
            .ToListAsync(cancellationToken);
        var productBySku = products.ToDictionary(p => p.Sku, StringComparer.Ordinal);

        var images = await _dbContext.ProductImages
            .AsNoTracking()
            .Where(i => baseSkus.Contains(i.ProductSku))
            .OrderBy(i => i.SortOrder)
            .ToListAsync(cancellationToken);
        var imagesByBaseSku = images
            .GroupBy(i => i.ProductSku, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(i => i.IsPrimary).ThenBy(i => i.SortOrder)
                      .Where(i => !string.IsNullOrWhiteSpace(i.Url))
                      .Select(i => i.Url)
                      .ToList(),
                StringComparer.Ordinal);

        var existingMappings = await _dbContext.TenantMarketplaceListingMaps
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId
                        && m.ClientId == clientId
                        && m.Provider == MarketplaceProvider.TikTokShop
                        && skus.Contains(m.SabrVariantSku))
            .Select(m => m.SabrVariantSku)
            .ToListAsync(cancellationToken);
        var alreadyMappedSkus = new HashSet<string>(existingMappings, StringComparer.Ordinal);

        var accessToken = tokenResult.Data;
        var result = new TikTokShopPublishResult();

        foreach (var sku in skus)
        {
            if (alreadyMappedSkus.Contains(sku))
            {
                result.AlreadyMapped++;
                result.Items.Add(new TikTokShopPublishItemResult
                {
                    SabrVariantSku = sku,
                    Status = "ALREADY_MAPPED",
                    Message = "Já publicado no TikTok Shop"
                });
                continue;
            }

            if (!variantBySku.TryGetValue(sku, out var variant)
                || !productBySku.TryGetValue(variant.BaseSku, out var product))
            {
                result.Failed++;
                result.Items.Add(new TikTokShopPublishItemResult
                {
                    SabrVariantSku = sku,
                    Status = "FAILED",
                    Message = "SKU ou produto não encontrado no catálogo"
                });
                continue;
            }

            try
            {
                var imageUrls = imagesByBaseSku.TryGetValue(variant.BaseSku, out var urls) ? urls : [];
                var uploadedImages = new List<TikTokShopProductImageItem>();

                foreach (var imageUrl in imageUrls.Take(9))
                {
                    var uploadResult = await _apiClient.UploadImageFromUrlAsync(
                        imageUrl, accessToken, _options.AppKey, _options.AppSecret, connection.ShopCipher, cancellationToken);

                    if (uploadResult.IsSuccess && !string.IsNullOrWhiteSpace(uploadResult.Data?.ImgId))
                    {
                        uploadedImages.Add(new TikTokShopProductImageItem { Id = uploadResult.Data.ImgId });
                    }
                }

                if (uploadedImages.Count == 0)
                {
                    result.Failed++;
                    result.Items.Add(new TikTokShopPublishItemResult
                    {
                        SabrVariantSku = sku,
                        Status = "FAILED",
                        Message = "Falha no upload das imagens"
                    });
                    continue;
                }

                var priceAmount = (product.CatalogPriceCents / 100m).ToString("F2",
                    System.Globalization.CultureInfo.InvariantCulture);

                TikTokShopPackageWeight? packageWeight = null;
                if (product.WeightKg.HasValue && product.WeightKg.Value > 0)
                {
                    packageWeight = new TikTokShopPackageWeight
                    {
                        Value = product.WeightKg.Value.ToString("F3",
                            System.Globalization.CultureInfo.InvariantCulture),
                        Unit = "KILOGRAM"
                    };
                }

                var createRequest = new TikTokShopCreateProductRequest
                {
                    Title = product.Name,
                    Description = product.Description ?? product.Name,
                    CategoryId = request.TikTokCategoryId,
                    Images = uploadedImages,
                    PackageWeight = packageWeight,
                    Skus =
                    [
                        new TikTokShopProductSkuItem
                        {
                            SellerSku = sku,
                            Price = new TikTokShopSkuPrice { Amount = priceAmount, Currency = "BRL" },
                            Inventory = [new TikTokShopSkuInventory { Quantity = variant.AvailableStock }]
                        }
                    ]
                };

                var createResponse = await _apiClient.CreateProductAsync(
                    createRequest, accessToken, _options.AppKey, _options.AppSecret, connection.ShopCipher, cancellationToken);

                if (!createResponse.IsSuccess || createResponse.Data == null)
                {
                    _logger.LogWarning(
                        "TikTok CreateProduct failed. sku={Sku} code={Code} message={Message}",
                        sku, createResponse.Code, createResponse.Message);

                    result.Failed++;
                    result.Items.Add(new TikTokShopPublishItemResult
                    {
                        SabrVariantSku = sku,
                        Status = "FAILED",
                        Message = $"TikTok API error: {createResponse.Message}"
                    });
                    continue;
                }

                var tikTokProductId = createResponse.Data.ProductId;
                var tikTokSkuId = createResponse.Data.Skus.FirstOrDefault()?.Id ?? string.Empty;

                var mapping = new TenantMarketplaceListingMap
                {
                    TenantId = tenantId,
                    ClientId = clientId,
                    Provider = MarketplaceProvider.TikTokShop,
                    SellerId = connection.SellerId,
                    MlItemId = tikTokProductId,
                    MlVariationId = string.IsNullOrWhiteSpace(tikTokSkuId) ? null : tikTokSkuId,
                    SabrVariantSku = sku
                };

                _dbContext.TenantMarketplaceListingMaps.Add(mapping);
                await _dbContext.SaveChangesAsync(cancellationToken);

                result.Published++;
                result.Items.Add(new TikTokShopPublishItemResult
                {
                    SabrVariantSku = sku,
                    TikTokItemId = tikTokProductId,
                    TikTokSkuId = tikTokSkuId,
                    Status = "PUBLISHED",
                    Message = "Publicado com sucesso"
                });

                _logger.LogInformation(
                    "TikTok product published. tenantId={TenantId} clientId={ClientId} sku={Sku} productId={ProductId}",
                    tenantId, clientId, sku, tikTokProductId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Exception publishing TikTok product. tenantId={TenantId} clientId={ClientId} sku={Sku}",
                    tenantId, clientId, sku);

                result.Failed++;
                result.Items.Add(new TikTokShopPublishItemResult
                {
                    SabrVariantSku = sku,
                    Status = "FAILED",
                    Message = "Erro interno ao publicar"
                });
            }
        }

        result.Total = skus.Count;
        return ServiceResult<TikTokShopPublishResult>.Success(result);
    }

    public async Task<ServiceResult<List<TikTokShopCategoryItem>>> GetCategoriesAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var tokenResult = await _oauthService.GetValidAccessTokenAsync(tenantId, clientId, cancellationToken);
        if (!tokenResult.Succeeded || tokenResult.Data == null)
        {
            return ServiceResult<List<TikTokShopCategoryItem>>.Failure(tokenResult.Errors);
        }

        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            c => c.TenantId == tenantId && c.ClientId == clientId && c.Provider == MarketplaceProvider.TikTokShop,
            cancellationToken);

        var response = await _apiClient.GetCategoriesAsync(
            tokenResult.Data, _options.AppKey, _options.AppSecret, connection?.ShopCipher, cancellationToken);

        if (!response.IsSuccess || response.Data == null)
        {
            return ServiceResult<List<TikTokShopCategoryItem>>.Failure(new[]
            {
                new ValidationError("categories", response.Message)
            });
        }

        var items = response.Data.CategoryList
            .Select(c => new TikTokShopCategoryItem
            {
                Id = c.Id,
                ParentId = c.ParentId,
                LocalName = c.LocalName,
                IsLeaf = c.IsLeaf,
                PermissionStatuses = c.PermissionStatuses
            })
            .ToList();

        return ServiceResult<List<TikTokShopCategoryItem>>.Success(items);
    }

    public async Task<ServiceResult<List<TikTokShopListingResult>>> ListListingsAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var maps = await _dbContext.TenantMarketplaceListingMaps
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId
                        && m.ClientId == clientId
                        && m.Provider == MarketplaceProvider.TikTokShop)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync(cancellationToken);

        var sabrSkus = maps.Select(m => m.SabrVariantSku).Distinct(StringComparer.Ordinal).ToList();

        var variants = await _dbContext.ProductVariants
            .AsNoTracking()
            .Where(v => sabrSkus.Contains(v.VariantSku))
            .ToListAsync(cancellationToken);
        var variantBySku = variants.ToDictionary(v => v.VariantSku, StringComparer.Ordinal);

        var baseSkus = variants.Select(v => v.BaseSku).Distinct(StringComparer.Ordinal).ToList();
        var products = await _dbContext.Products
            .AsNoTracking()
            .Where(p => baseSkus.Contains(p.Sku))
            .ToListAsync(cancellationToken);
        var productBySku = products.ToDictionary(p => p.Sku, StringComparer.Ordinal);

        var listings = maps.Select(m =>
        {
            variantBySku.TryGetValue(m.SabrVariantSku, out var variant);
            var productName = variant != null && productBySku.TryGetValue(variant.BaseSku, out var product)
                ? product.Name
                : null;

            return new TikTokShopListingResult
            {
                Id = m.Id,
                TikTokItemId = m.MlItemId,
                TikTokSkuId = m.MlVariationId,
                SabrVariantSku = m.SabrVariantSku,
                ProductName = productName,
                CreatedAt = m.CreatedAt
            };
        }).ToList();

        return ServiceResult<List<TikTokShopListingResult>>.Success(listings);
    }
}

public sealed class TikTokShopPublishRequest
{
    public List<string> SabrVariantSkus { get; set; } = [];
    public string TikTokCategoryId { get; set; } = string.Empty;
}

public sealed class TikTokShopPublishValidateResult
{
    public int Total { get; set; }
    public int Eligible { get; set; }
    public int Ineligible { get; set; }
    public List<TikTokShopPublishValidateItem> Items { get; set; } = [];
}

public sealed class TikTokShopPublishValidateItem
{
    public string SabrVariantSku { get; set; } = string.Empty;
    public string? ProductName { get; set; }
    public long PriceCents { get; set; }
    public int ImageCount { get; set; }
    public bool Eligible { get; set; }
    public bool AlreadyMapped { get; set; }
    public List<string> Reasons { get; set; } = [];
}

public sealed class TikTokShopPublishResult
{
    public int Total { get; set; }
    public int Published { get; set; }
    public int AlreadyMapped { get; set; }
    public int Failed { get; set; }
    public List<TikTokShopPublishItemResult> Items { get; set; } = [];
}

public sealed class TikTokShopPublishItemResult
{
    public string SabrVariantSku { get; set; } = string.Empty;
    public string? TikTokItemId { get; set; }
    public string? TikTokSkuId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
}

public sealed class TikTokShopListingResult
{
    public Guid Id { get; set; }
    public string TikTokItemId { get; set; } = string.Empty;
    public string? TikTokSkuId { get; set; }
    public string SabrVariantSku { get; set; } = string.Empty;
    public string? ProductName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TikTokShopCategoryItem
{
    public string Id { get; set; } = string.Empty;
    public string ParentId { get; set; } = string.Empty;
    public string LocalName { get; set; } = string.Empty;
    public bool IsLeaf { get; set; }
    public List<string> PermissionStatuses { get; set; } = [];
}
