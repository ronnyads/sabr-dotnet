using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sabr.Application.Abstractions;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;

namespace Sabr.Application.Services;

public sealed class ProductVariantBackfillService
{
    private readonly IAppDbContext _dbContext;
    private readonly ILogger<ProductVariantBackfillService> _logger;

    public ProductVariantBackfillService(
        IAppDbContext dbContext,
        ILogger<ProductVariantBackfillService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<ProductVariantBackfillResult> RunOnceAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        var safeBatchSize = Math.Max(1, batchSize);
        var missingSkus = await (
                from publication in _dbContext.Publications.AsNoTracking()
                join variant in _dbContext.ProductVariants.AsNoTracking()
                    on publication.ProductSku equals variant.VariantSku into variants
                from variant in variants.DefaultIfEmpty()
                where publication.Status == PublicationStatus.Draft
                      && variant == null
                select publication.ProductSku)
            .Distinct()
            .Take(safeBatchSize)
            .ToListAsync(cancellationToken);

        var result = new ProductVariantBackfillResult
        {
            Processed = missingSkus.Count
        };
        if (missingSkus.Count == 0)
        {
            return result;
        }

        var products = await _dbContext.Products.AsNoTracking()
            .Where(item => missingSkus.Contains(item.Sku))
            .ToDictionaryAsync(item => item.Sku, item => item, StringComparer.Ordinal, cancellationToken);

        foreach (var sku in missingSkus)
        {
            try
            {
                if (await _dbContext.ProductVariants.AnyAsync(item => item.VariantSku == sku, cancellationToken))
                {
                    result.AlreadyExists++;
                    continue;
                }

                if (!products.TryGetValue(sku, out var product))
                {
                    result.SkippedMissingProduct++;
                    continue;
                }

                _dbContext.ProductVariants.Add(new ProductVariant
                {
                    VariantSku = sku,
                    BaseSku = sku,
                    Name = string.IsNullOrWhiteSpace(product.Name) ? sku : product.Name,
                    CostPriceCents = Math.Max(0, product.CostPriceCents),
                    CatalogPriceCents = Math.Max(0, product.CatalogPriceCents),
                    PhysicalStock = 0,
                    ReservedStock = 0,
                    AvailableStock = 0,
                    IsActive = product.IsActive,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });

                await _dbContext.SaveChangesAsync(cancellationToken);
                result.Created++;
            }
            catch (Exception ex)
            {
                result.Errors++;
                _logger.LogWarning(
                    ex,
                    "ProductVariant backfill failed for sku={Sku}.",
                    sku);
            }
        }

        return result;
    }
}

public sealed class ProductVariantBackfillResult
{
    public int Processed { get; set; }
    public int Created { get; set; }
    public int SkippedMissingProduct { get; set; }
    public int AlreadyExists { get; set; }
    public int Errors { get; set; }
}

