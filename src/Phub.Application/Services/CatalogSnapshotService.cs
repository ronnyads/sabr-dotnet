using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Stock;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.ValueObjects;

namespace Phub.Application.Services;

public sealed class CatalogSnapshotService
{
    private readonly IAppDbContext _dbContext;
    private readonly ILogger<CatalogSnapshotService> _logger;

    public CatalogSnapshotService(
        IAppDbContext dbContext,
        ILogger<CatalogSnapshotService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<ServiceResult<CatalogVariantSnapshotResult>> GetVariantSnapshotAsync(
        string tenantId,
        Guid clientId,
        Guid actorId,
        CatalogVariantSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty)
        {
            return Failure<CatalogVariantSnapshotResult>("context", "INVALID_CONTEXT");
        }

        if (string.IsNullOrWhiteSpace(request.VariantSku))
        {
            return Failure<CatalogVariantSnapshotResult>("variantSku", "VARIANT_SKU_REQUIRED");
        }

        var normalizedVariantSku = Sku.Normalize(request.VariantSku);
        var resolutionContext = await ResolveVariantContextAsync(
            tenantId,
            actorId == Guid.Empty ? clientId : actorId,
            normalizedVariantSku,
            cancellationToken);
        if (resolutionContext == null)
        {
            return Failure<CatalogVariantSnapshotResult>("variantSku", "PRODUCT_NOT_FOUND");
        }

        var resolved = VariantStockResolver.Resolve(normalizedVariantSku, resolutionContext.Variants);
        var resolvedVariant = !string.IsNullOrWhiteSpace(resolved.ResolvedVariantSku)
            ? resolutionContext.Variants.FirstOrDefault(item => string.Equals(item.VariantSku, resolved.ResolvedVariantSku, StringComparison.Ordinal))
            : null;
        var snapshotVariant = resolvedVariant
                              ?? resolutionContext.ExactVariant
                              ?? resolutionContext.Variants.OrderBy(item => item.VariantSku, StringComparer.Ordinal).FirstOrDefault();

        var baseSku = resolutionContext.BaseSku;
        var baseProduct = await _dbContext.Products.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Sku == baseSku, cancellationToken);
        if (baseProduct == null)
        {
            return Failure<CatalogVariantSnapshotResult>("variantSku", "PRODUCT_NOT_FOUND");
        }

        var images = await _dbContext.ProductImages.AsNoTracking()
            .Where(item => item.ProductSku == baseSku)
            .OrderByDescending(item => item.IsPrimary)
            .ThenBy(item => item.SortOrder)
            .ThenBy(item => item.CreatedAt)
            .Select(item => item.Url)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(10)
            .ToListAsync(cancellationToken);

        var title = BuildTitle(baseProduct.Name, snapshotVariant?.Name, normalizedVariantSku);
        var effectiveCostPriceCents = snapshotVariant != null && snapshotVariant.CostPriceCents > 0
            ? snapshotVariant.CostPriceCents
            : baseProduct.CostPriceCents;
        var effectiveCatalogPriceCents = snapshotVariant != null && snapshotVariant.CatalogPriceCents > 0
            ? snapshotVariant.CatalogPriceCents
            : baseProduct.CatalogPriceCents > 0
                ? baseProduct.CatalogPriceCents
                : (long?)null;
        var result = new CatalogVariantSnapshotResult
        {
            VariantSku = snapshotVariant?.VariantSku ?? normalizedVariantSku,
            BaseSku = baseSku,
            Title = title,
            Description = baseProduct.Description,
            CostPrice = ListingDraftHelpers.ToDecimal(Math.Max(0, effectiveCostPriceCents)),
            CatalogPrice = effectiveCatalogPriceCents.HasValue
                ? ListingDraftHelpers.ToDecimal(Math.Max(0, effectiveCatalogPriceCents.Value))
                : null,
            CurrencyId = "BRL",
            StockAvailable = resolved.AvailableStock,
            ResolvedVariantSku = resolved.ResolvedVariantSku,
            StockSource = resolved.StockSource.ToString(),
            Gtin = baseProduct.Ean,
            Ncm = baseProduct.Ncm,
            Origin = "Nacional",
            Brand = string.IsNullOrWhiteSpace(baseProduct.Brand) ? null : baseProduct.Brand.Trim(),
            ListingTypeDefault = "gold_special",
            SiteId = "MLB",
            Images = images
                .Select((url, index) => new CatalogVariantSnapshotImageResult
                {
                    Url = url,
                    Position = index + 1
                })
                .ToList(),
            Dimensions = new CatalogVariantSnapshotDimensionsResult
            {
                WeightKg = baseProduct.WeightKg,
                HeightCm = baseProduct.HeightCm,
                WidthCm = baseProduct.WidthCm,
                LengthCm = baseProduct.LengthCm
            },
            VariantBackfilled = resolutionContext.VariantBackfilled
        };

        _logger.LogInformation(
            "Stock resolved. productSku={ProductSku} requestedVariantSku={Requested} resolvedVariantSku={Resolved} source={Source} stock={Stock}",
            baseSku,
            normalizedVariantSku,
            resolved.ResolvedVariantSku,
            resolved.StockSource,
            resolved.AvailableStock);

        result.QualityIssues = BuildQualityIssues(result);
        return ServiceResult<CatalogVariantSnapshotResult>.Success(result);
    }

    private async Task<VariantResolutionContext?> ResolveVariantContextAsync(
        string tenantId,
        Guid actorId,
        string normalizedVariantSku,
        CancellationToken cancellationToken)
    {
        var exactVariant = await _dbContext.ProductVariants
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.VariantSku == normalizedVariantSku, cancellationToken);
        if (exactVariant != null)
        {
            var exactVariants = await _dbContext.ProductVariants
                .AsNoTracking()
                .Where(item => item.BaseSku == exactVariant.BaseSku)
                .ToListAsync(cancellationToken);
            if (exactVariants.Count == 0)
            {
                exactVariants.Add(exactVariant);
            }

            return new VariantResolutionContext(exactVariant.BaseSku, exactVariants, false, exactVariant);
        }

        var baseProduct = await _dbContext.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Sku == normalizedVariantSku && item.IsActive, cancellationToken);
        if (baseProduct == null)
        {
            return null;
        }

        var variants = await _dbContext.ProductVariants
            .AsNoTracking()
            .Where(item => item.BaseSku == baseProduct.Sku)
            .ToListAsync(cancellationToken);
        if (variants.Count > 0)
        {
            return new VariantResolutionContext(baseProduct.Sku, variants, false, null);
        }

        var createdVariant = new ProductVariant
        {
            VariantSku = baseProduct.Sku,
            BaseSku = baseProduct.Sku,
            Name = string.IsNullOrWhiteSpace(baseProduct.Name) ? baseProduct.Sku : baseProduct.Name.Trim(),
            CostPriceCents = Math.Max(0, baseProduct.CostPriceCents),
            CatalogPriceCents = Math.Max(0, baseProduct.CatalogPriceCents),
            PhysicalStock = 0,
            ReservedStock = 0,
            AvailableStock = 0,
            IsActive = baseProduct.IsActive,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.ProductVariants.Add(createdVariant);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            await AppendBackfillAuditAsync(tenantId, actorId, normalizedVariantSku, cancellationToken);
            return new VariantResolutionContext(baseProduct.Sku, new List<ProductVariant> { createdVariant }, true, createdVariant);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogInformation(
                ex,
                "ProductVariant snapshot backfill had race for sku={Sku}. Reloading existing variants.",
                normalizedVariantSku);
            var reloaded = await _dbContext.ProductVariants
                .AsNoTracking()
                .Where(item => item.BaseSku == baseProduct.Sku)
                .ToListAsync(cancellationToken);
            if (reloaded.Count == 0)
            {
                throw;
            }

            var reloadedExact = reloaded.FirstOrDefault(item => string.Equals(item.VariantSku, normalizedVariantSku, StringComparison.Ordinal));
            return new VariantResolutionContext(baseProduct.Sku, reloaded, false, reloadedExact);
        }
    }

    private async Task AppendBackfillAuditAsync(
        string tenantId,
        Guid actorId,
        string sku,
        CancellationToken cancellationToken)
    {
        _dbContext.AuditEvents.Add(new AuditEvent
        {
            TenantId = tenantId,
            ActorType = "ClientUser",
            ActorId = actorId == Guid.Empty ? null : actorId,
            Action = "PRODUCTVARIANT_BACKFILLED",
            Entity = nameof(ProductVariant),
            EntityId = null,
            RequestId = Guid.NewGuid(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                sku
            })
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static List<CatalogVariantSnapshotIssueResult> BuildQualityIssues(CatalogVariantSnapshotResult snapshot)
    {
        var issues = new List<CatalogVariantSnapshotIssueResult>();
        if (string.IsNullOrWhiteSpace(snapshot.Gtin))
        {
            issues.Add(new CatalogVariantSnapshotIssueResult
            {
                Code = "MISSING_GTIN",
                FieldPath = "gtin",
                Severity = "warning",
                Message = "GTIN ausente."
            });
        }

        if (string.IsNullOrWhiteSpace(snapshot.Ncm))
        {
            issues.Add(new CatalogVariantSnapshotIssueResult
            {
                Code = "MISSING_NCM",
                FieldPath = "ncm",
                Severity = "warning",
                Message = "NCM ausente."
            });
        }

        if (snapshot.Images.Count == 0)
        {
            issues.Add(new CatalogVariantSnapshotIssueResult
            {
                Code = "MISSING_IMAGES",
                FieldPath = "images",
                Severity = "warning",
                Message = "Nenhuma imagem cadastrada para o produto."
            });
        }

        return issues;
    }

    private static string BuildTitle(string? productName, string? variantName, string fallbackSku)
    {
        var product = string.IsNullOrWhiteSpace(productName) ? string.Empty : productName.Trim();
        var variant = string.IsNullOrWhiteSpace(variantName) ? string.Empty : variantName.Trim();
        if (string.IsNullOrWhiteSpace(product) && string.IsNullOrWhiteSpace(variant))
        {
            return fallbackSku;
        }

        if (string.IsNullOrWhiteSpace(variant) || string.Equals(product, variant, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(product) ? variant : product;
        }

        return $"{product} - {variant}";
    }

    private static ServiceResult<T> Failure<T>(string field, string code)
    {
        return ServiceResult<T>.Failure(new[] { new ValidationError(field, code) });
    }

    private sealed record VariantResolutionContext(
        string BaseSku,
        List<ProductVariant> Variants,
        bool VariantBackfilled,
        ProductVariant? ExactVariant);
}
