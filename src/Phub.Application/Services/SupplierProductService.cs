using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class SupplierProductService
{
    private readonly IAppDbContext _dbContext;

    public SupplierProductService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ServiceResult<SupplierProductResult>> CreateDraftAsync(
        Guid supplierId,
        SupplierProductUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateRequest(request);
        if (errors.Count > 0)
            return ServiceResult<SupplierProductResult>.Failure(errors);

        var product = new SupplierProduct
        {
            SupplierId = supplierId,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            Brand = request.Brand?.Trim() ?? string.Empty,
            Ncm = request.Ncm?.Trim(),
            Ean = request.Ean?.Trim(),
            CostPriceCents = request.CostPriceCents,
            Status = SupplierProductStatus.Draft,
            Images = request.Images,
            WidthCm = request.WidthCm,
            HeightCm = request.HeightCm,
            LengthCm = request.LengthCm,
            WeightKg = request.WeightKg
        };

        _dbContext.SupplierProducts.Add(product);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<SupplierProductResult>.Success(MapResult(product));
    }

    public async Task<ServiceResult<List<SupplierProductResult>>> ListAsync(
        Guid supplierId,
        CancellationToken cancellationToken = default)
    {
        var products = await _dbContext.SupplierProducts
            .Where(p => p.SupplierId == supplierId)
            .OrderByDescending(p => p.UpdatedAt)
            .ToListAsync(cancellationToken);

        return ServiceResult<List<SupplierProductResult>>.Success(products.Select(MapResult).ToList());
    }

    public async Task<ServiceResult<SupplierProductResult>> GetAsync(
        Guid supplierId,
        Guid productId,
        CancellationToken cancellationToken = default)
    {
        var product = await _dbContext.SupplierProducts
            .FirstOrDefaultAsync(p => p.Id == productId && p.SupplierId == supplierId, cancellationToken);

        if (product == null)
            return ServiceResult<SupplierProductResult>.NotFound("id", "Product not found");

        return ServiceResult<SupplierProductResult>.Success(MapResult(product));
    }

    public async Task<ServiceResult<SupplierProductResult>> UpdateAsync(
        Guid supplierId,
        Guid productId,
        SupplierProductUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var product = await _dbContext.SupplierProducts
            .FirstOrDefaultAsync(p => p.Id == productId && p.SupplierId == supplierId, cancellationToken);

        if (product == null)
            return ServiceResult<SupplierProductResult>.NotFound("id", "Product not found");

        if (product.Status != SupplierProductStatus.Draft && product.Status != SupplierProductStatus.AdjustmentRequested)
        {
            return ServiceResult<SupplierProductResult>.Failure(new[]
            {
                new ValidationError("status", "Only drafts or adjustment-requested products can be edited")
            });
        }

        var errors = ValidateRequest(request);
        if (errors.Count > 0)
            return ServiceResult<SupplierProductResult>.Failure(errors);

        product.Name = request.Name.Trim();
        product.Description = request.Description?.Trim() ?? string.Empty;
        product.Brand = request.Brand?.Trim() ?? string.Empty;
        product.Ncm = request.Ncm?.Trim();
        product.Ean = request.Ean?.Trim();
        product.CostPriceCents = request.CostPriceCents;
        product.Images = request.Images;
        product.WidthCm = request.WidthCm;
        product.HeightCm = request.HeightCm;
        product.LengthCm = request.LengthCm;
        product.WeightKg = request.WeightKg;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<SupplierProductResult>.Success(MapResult(product));
    }

    public async Task<ServiceResult<SupplierProductResult>> SubmitForReviewAsync(
        Guid supplierId,
        Guid productId,
        CancellationToken cancellationToken = default)
    {
        var product = await _dbContext.SupplierProducts
            .FirstOrDefaultAsync(p => p.Id == productId && p.SupplierId == supplierId, cancellationToken);

        if (product == null)
            return ServiceResult<SupplierProductResult>.NotFound("id", "Product not found");

        if (product.Status != SupplierProductStatus.Draft && product.Status != SupplierProductStatus.AdjustmentRequested)
        {
            return ServiceResult<SupplierProductResult>.Failure(new[]
            {
                new ValidationError("status", "Only drafts or adjustment-requested products can be submitted")
            });
        }

        if (string.IsNullOrWhiteSpace(product.Name) || product.CostPriceCents <= 0)
        {
            return ServiceResult<SupplierProductResult>.Failure(new[]
            {
                new ValidationError("product", "Name and cost price are required before submitting")
            });
        }

        product.Status = SupplierProductStatus.PendingReview;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<SupplierProductResult>.Success(MapResult(product));
    }

    public async Task<ServiceResult<bool>> DeleteAsync(
        Guid supplierId,
        Guid productId,
        CancellationToken cancellationToken = default)
    {
        var product = await _dbContext.SupplierProducts
            .FirstOrDefaultAsync(p => p.Id == productId && p.SupplierId == supplierId, cancellationToken);

        if (product == null)
            return ServiceResult<bool>.NotFound("id", "Product not found");

        if (product.Status != SupplierProductStatus.Draft)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("status", "Only draft products can be deleted")
            });
        }

        _dbContext.SupplierProducts.Remove(product);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    private static List<ValidationError> ValidateRequest(SupplierProductUpsertRequest request)
    {
        var errors = new List<ValidationError>();
        if (string.IsNullOrWhiteSpace(request.Name))
            errors.Add(new ValidationError("name", "Name is required"));
        if (request.CostPriceCents < 0)
            errors.Add(new ValidationError("costPriceCents", "Cost price cannot be negative"));
        return errors;
    }

    public static SupplierProductResult MapResult(SupplierProduct p) => new()
    {
        Id = p.Id,
        SupplierId = p.SupplierId,
        Name = p.Name,
        Description = p.Description,
        Brand = p.Brand,
        Ncm = p.Ncm,
        Ean = p.Ean,
        CostPriceCents = p.CostPriceCents,
        PlatformMarginPercent = p.PlatformMarginPercent,
        Status = p.Status.ToString(),
        AdminNotes = p.AdminNotes,
        LinkedProductSku = p.LinkedProductSku,
        Images = p.Images,
        WidthCm = p.WidthCm,
        HeightCm = p.HeightCm,
        LengthCm = p.LengthCm,
        WeightKg = p.WeightKg,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
        ApprovedAt = p.ApprovedAt
    };
}
