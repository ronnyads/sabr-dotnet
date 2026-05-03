using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Domain.ValueObjects;

namespace Phub.Application.Services;

public sealed class SupplierProductApprovalService
{
    private readonly IAppDbContext _dbContext;

    public SupplierProductApprovalService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ServiceResult<List<SupplierProductResult>>> ListPendingAsync(
        CancellationToken cancellationToken = default)
    {
        var products = await _dbContext.SupplierProducts
            .Where(p => p.Status == SupplierProductStatus.PendingReview)
            .OrderBy(p => p.UpdatedAt)
            .ToListAsync(cancellationToken);

        return ServiceResult<List<SupplierProductResult>>.Success(
            products.Select(SupplierProductService.MapResult).ToList());
    }

    public async Task<ServiceResult<SupplierProductResult>> GetAsync(
        Guid productId,
        CancellationToken cancellationToken = default)
    {
        var product = await _dbContext.SupplierProducts
            .FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);

        if (product == null)
            return ServiceResult<SupplierProductResult>.NotFound("id", "Product not found");

        return ServiceResult<SupplierProductResult>.Success(SupplierProductService.MapResult(product));
    }

    public async Task<ServiceResult<SupplierProductResult>> ApproveAsync(
        Guid productId,
        Guid approverUserId,
        AdminApproveSupplierProductRequest request,
        CancellationToken cancellationToken = default)
    {
        var product = await _dbContext.SupplierProducts
            .FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);

        if (product == null)
            return ServiceResult<SupplierProductResult>.NotFound("id", "Product not found");

        if (product.Status != SupplierProductStatus.PendingReview)
        {
            return ServiceResult<SupplierProductResult>.Failure(new[]
            {
                new ValidationError("status", "Only pending-review products can be approved")
            });
        }

        if (request.MarginPercent < 0 || request.MarginPercent > 100)
        {
            return ServiceResult<SupplierProductResult>.Failure(new[]
            {
                new ValidationError("marginPercent", "Margin must be between 0 and 100")
            });
        }

        var sku = GenerateSku(product);
        var catalogPrice = CalculateCatalogPrice(product.CostPriceCents, request.MarginPercent);

        var catalogProduct = new Product
        {
            Sku = sku,
            Name = product.Name,
            Brand = product.Brand,
            Description = product.Description,
            Ncm = product.Ncm,
            Ean = product.Ean,
            CategoryId = product.CategoryId?.ToString(),
            CostPriceCents = product.CostPriceCents,
            CatalogPriceCents = catalogPrice,
            WidthCm = product.WidthCm,
            HeightCm = product.HeightCm,
            LengthCm = product.LengthCm,
            WeightKg = product.WeightKg,
            IsActive = true
        };

        var variant = new ProductVariant
        {
            VariantSku = sku,
            BaseSku = sku,
            Name = product.Name,
            CostPriceCents = product.CostPriceCents,
            CatalogPriceCents = catalogPrice,
            IsActive = true
        };

        var catalogLink = new ProductCatalog
        {
            CatalogId = request.CatalogId,
            ProductSku = sku
        };

        product.Status = SupplierProductStatus.Approved;
        product.PlatformMarginPercent = request.MarginPercent;
        product.LinkedProductSku = sku;
        product.AdminNotes = request.Notes;
        product.ApprovedAt = DateTimeOffset.UtcNow;
        product.ApprovedByPlatformUserId = approverUserId == Guid.Empty ? null : approverUserId;

        _dbContext.Products.Add(catalogProduct);
        _dbContext.ProductVariants.Add(variant);
        _dbContext.ProductCatalogs.Add(catalogLink);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<SupplierProductResult>.Success(SupplierProductService.MapResult(product));
    }

    public async Task<ServiceResult<SupplierProductResult>> RejectAsync(
        Guid productId,
        AdminRejectSupplierProductRequest request,
        CancellationToken cancellationToken = default)
    {
        var product = await _dbContext.SupplierProducts
            .FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);

        if (product == null)
            return ServiceResult<SupplierProductResult>.NotFound("id", "Product not found");

        if (product.Status != SupplierProductStatus.PendingReview)
        {
            return ServiceResult<SupplierProductResult>.Failure(new[]
            {
                new ValidationError("status", "Only pending-review products can be rejected")
            });
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return ServiceResult<SupplierProductResult>.Failure(new[]
            {
                new ValidationError("reason", "Rejection reason is required")
            });
        }

        product.Status = SupplierProductStatus.Rejected;
        product.AdminNotes = request.Reason.Trim();

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<SupplierProductResult>.Success(SupplierProductService.MapResult(product));
    }

    public async Task<ServiceResult<SupplierProductResult>> RequestAdjustmentAsync(
        Guid productId,
        AdminRequestAdjustmentRequest request,
        CancellationToken cancellationToken = default)
    {
        var product = await _dbContext.SupplierProducts
            .FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);

        if (product == null)
            return ServiceResult<SupplierProductResult>.NotFound("id", "Product not found");

        if (product.Status != SupplierProductStatus.PendingReview)
        {
            return ServiceResult<SupplierProductResult>.Failure(new[]
            {
                new ValidationError("status", "Only pending-review products can have adjustment requested")
            });
        }

        if (string.IsNullOrWhiteSpace(request.Notes))
        {
            return ServiceResult<SupplierProductResult>.Failure(new[]
            {
                new ValidationError("notes", "Adjustment notes are required")
            });
        }

        product.Status = SupplierProductStatus.AdjustmentRequested;
        product.AdminNotes = request.Notes.Trim();

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<SupplierProductResult>.Success(SupplierProductService.MapResult(product));
    }

    private static string GenerateSku(SupplierProduct product)
    {
        var prefix = "SUP";
        var namePart = new string(
            product.Name
                .ToUpperInvariant()
                .Where(c => char.IsLetterOrDigit(c))
                .Take(8)
                .ToArray());
        var suffix = product.Id.ToString("N")[..6].ToUpperInvariant();
        return Sku.Normalize($"{prefix}-{namePart}-{suffix}");
    }

    private static long CalculateCatalogPrice(long costPriceCents, decimal marginPercent)
    {
        var margin = marginPercent / 100m;
        return (long)Math.Ceiling(costPriceCents / (1m - margin));
    }
}
