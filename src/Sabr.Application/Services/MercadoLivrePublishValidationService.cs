using Microsoft.EntityFrameworkCore;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Validation;
using Sabr.Domain.ValueObjects;

namespace Sabr.Application.Services;

public sealed class MercadoLivrePublishValidationService
{
    private readonly IAppDbContext _dbContext;

    public MercadoLivrePublishValidationService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ServiceResult<MercadoLivrePublishValidateResult>> ValidateAsync(
        string tenantId,
        Guid clientId,
        MercadoLivrePublishValidateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty)
        {
            return ServiceResult<MercadoLivrePublishValidateResult>.Failure(new[]
            {
                new ValidationError("context", "Invalid tenant/client context")
            });
        }

        var variantSkus = await ResolveVariantSkusAsync(tenantId, clientId, request, cancellationToken);
        if (variantSkus.Count == 0)
        {
            return ServiceResult<MercadoLivrePublishValidateResult>.Failure(new[]
            {
                new ValidationError("scope", "No variants found for validation")
            });
        }

        var variants = await _dbContext.ProductVariants
            .AsNoTracking()
            .Where(item => variantSkus.Contains(item.VariantSku))
            .ToListAsync(cancellationToken);
        var variantsBySku = variants.ToDictionary(item => item.VariantSku, StringComparer.Ordinal);

        var baseSkus = variants.Select(item => item.BaseSku).Distinct(StringComparer.Ordinal).ToList();
        var products = await _dbContext.Products
            .AsNoTracking()
            .Where(item => baseSkus.Contains(item.Sku))
            .ToListAsync(cancellationToken);
        var productsBySku = products.ToDictionary(item => item.Sku, StringComparer.Ordinal);

        var imagesBySku = await _dbContext.ProductImages
            .AsNoTracking()
            .Where(item => baseSkus.Contains(item.ProductSku))
            .GroupBy(item => item.ProductSku)
            .Select(group => new { ProductSku = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var imageCountBySku = imagesBySku.ToDictionary(item => item.ProductSku, item => item.Count, StringComparer.Ordinal);

        var items = new List<MercadoLivrePublishValidationItemResult>(variantSkus.Count);
        foreach (var sku in variantSkus)
        {
            var reasons = new List<string>();
            if (!variantsBySku.TryGetValue(sku, out var variant))
            {
                reasons.Add("VARIANT_NOT_FOUND");
            }
            else
            {
                if (!variant.IsActive)
                {
                    reasons.Add("VARIANT_INACTIVE");
                }

                if (variant.CatalogPriceCents <= 0)
                {
                    reasons.Add("MISSING_PRICE");
                }

                if (!productsBySku.TryGetValue(variant.BaseSku, out var product))
                {
                    reasons.Add("PRODUCT_NOT_FOUND");
                }
                else
                {
                    var imageCount = imageCountBySku.TryGetValue(product.Sku, out var count) ? count : 0;
                    if (imageCount <= 0)
                    {
                        reasons.Add("MISSING_IMAGE");
                    }

                    if (!HasPositiveDimensions(product))
                    {
                        reasons.Add("MISSING_DIMENSIONS");
                    }
                }
            }

            items.Add(new MercadoLivrePublishValidationItemResult
            {
                SabrVariantSku = sku,
                Eligible = reasons.Count == 0,
                Reasons = reasons
            });
        }

        var result = new MercadoLivrePublishValidateResult
        {
            Total = items.Count,
            Eligible = items.Count(item => item.Eligible),
            Ineligible = items.Count(item => !item.Eligible),
            Items = items
        };

        return ServiceResult<MercadoLivrePublishValidateResult>.Success(result);
    }

    private async Task<List<string>> ResolveVariantSkusAsync(
        string tenantId,
        Guid clientId,
        MercadoLivrePublishValidateRequest request,
        CancellationToken cancellationToken)
    {
        var directSkus = (request.SabrVariantSkus ?? new List<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => Sku.Normalize(item))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (directSkus.Count > 0)
        {
            return directSkus;
        }

        if (request.CatalogId.HasValue)
        {
            var catalogId = request.CatalogId.Value;
            var productSkus = await _dbContext.ProductCatalogs
                .AsNoTracking()
                .Where(item => item.TenantId == tenantId && item.CatalogId == catalogId)
                .Select(item => item.ProductSku)
                .Distinct()
                .ToListAsync(cancellationToken);
            if (productSkus.Count == 0)
            {
                return new List<string>();
            }

            return await _dbContext.ProductVariants
                .AsNoTracking()
                .Where(item => productSkus.Contains(item.BaseSku))
                .Select(item => item.VariantSku)
                .Distinct()
                .ToListAsync(cancellationToken);
        }

        if (request.PlanId.HasValue)
        {
            var planId = request.PlanId.Value;
            var hasActiveSubscription = await _dbContext.ClientPlanSubscriptions
                .AsNoTracking()
                .AnyAsync(item => item.TenantId == tenantId
                                  && item.ClientId == clientId
                                  && item.PlanId == planId
                                  && item.IsActive,
                    cancellationToken);
            if (!hasActiveSubscription)
            {
                return new List<string>();
            }

            var catalogIds = await _dbContext.PlanCatalogs
                .AsNoTracking()
                .Where(item => item.TenantId == tenantId && item.PlanId == planId)
                .Select(item => item.CatalogId)
                .Distinct()
                .ToListAsync(cancellationToken);

            var productSkus = await _dbContext.ProductCatalogs
                .AsNoTracking()
                .Where(item => item.TenantId == tenantId && catalogIds.Contains(item.CatalogId))
                .Select(item => item.ProductSku)
                .Distinct()
                .ToListAsync(cancellationToken);
            if (productSkus.Count == 0)
            {
                return new List<string>();
            }

            return await _dbContext.ProductVariants
                .AsNoTracking()
                .Where(item => productSkus.Contains(item.BaseSku))
                .Select(item => item.VariantSku)
                .Distinct()
                .ToListAsync(cancellationToken);
        }

        return new List<string>();
    }

    private static bool HasPositiveDimensions(Domain.Entities.Product product)
    {
        return product.WidthCm.GetValueOrDefault() > 0
               && product.HeightCm.GetValueOrDefault() > 0
               && product.LengthCm.GetValueOrDefault() > 0
               && product.WeightKg.GetValueOrDefault() > 0;
    }
}
