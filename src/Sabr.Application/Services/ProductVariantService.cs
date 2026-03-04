using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Validation;
using Sabr.Domain.Entities;
using Sabr.Domain.ValueObjects;

namespace Sabr.Application.Services;

public sealed class ProductVariantService
{
    private readonly IAppDbContext _dbContext;

    public ProductVariantService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ServiceResult<IReadOnlyCollection<AdminProductVariantResult>>> ListAsync(
        string baseSku,
        CancellationToken cancellationToken = default)
    {
        if (!Sku.TryParse(baseSku, out var parsedBaseSku))
        {
            return ServiceResult<IReadOnlyCollection<AdminProductVariantResult>>.Failure(new[]
            {
                new ValidationError("sku", "SKU format is invalid")
            });
        }

        var baseProductExists = await _dbContext.Products
            .AsNoTracking()
            .AnyAsync(item => item.Sku == parsedBaseSku.Value, cancellationToken);

        if (!baseProductExists)
        {
            return ServiceResult<IReadOnlyCollection<AdminProductVariantResult>>.Failure(new[]
            {
                new ValidationError("sku", "Product not found")
            });
        }

        var variants = await _dbContext.ProductVariants
            .AsNoTracking()
            .Where(item => item.BaseSku == parsedBaseSku.Value)
            .OrderBy(item => item.VariantSku)
            .ToListAsync(cancellationToken);

        return ServiceResult<IReadOnlyCollection<AdminProductVariantResult>>.Success(variants.Select(Map).ToList());
    }

    public async Task<ServiceResult<AdminProductVariantResult>> CreateAsync(
        string baseSku,
        AdminProductVariantCreateRequest request,
        Guid actorUserId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateCreateRequest(baseSku, request, actorUserId);
        if (errors.Count > 0)
        {
            return ServiceResult<AdminProductVariantResult>.Failure(errors);
        }

        var normalizedBaseSku = Sku.Normalize(baseSku);
        var normalizedVariantSku = Sku.Normalize(request.VariantSku);

        var product = await _dbContext.Products.FirstOrDefaultAsync(item => item.Sku == normalizedBaseSku, cancellationToken);
        if (product == null)
        {
            return ServiceResult<AdminProductVariantResult>.Failure(new[]
            {
                new ValidationError("sku", "Product not found")
            });
        }

        var existingVariant = await _dbContext.ProductVariants.FirstOrDefaultAsync(
            item => item.VariantSku == normalizedVariantSku,
            cancellationToken);

        if (existingVariant != null)
        {
            return ServiceResult<AdminProductVariantResult>.Failure(new[]
            {
                new ValidationError("variantSku", "Variant SKU already exists")
            });
        }

        var name = string.IsNullOrWhiteSpace(request.Name) ? product.Name : request.Name.Trim();
        var costPriceCents = request.CostPriceCents ?? product.CostPriceCents;
        var catalogPriceCents = request.CatalogPriceCents ?? product.CatalogPriceCents;
        var physicalStock = request.PhysicalStock ?? 0;
        var reservedStock = request.ReservedStock ?? 0;
        var isActive = request.IsActive ?? true;

        errors = ValidateVariantFields(name, costPriceCents, catalogPriceCents, physicalStock, reservedStock);
        if (errors.Count > 0)
        {
            return ServiceResult<AdminProductVariantResult>.Failure(errors);
        }

        var variant = new ProductVariant
        {
            VariantSku = normalizedVariantSku,
            BaseSku = normalizedBaseSku,
            Name = name,
            CostPriceCents = costPriceCents,
            CatalogPriceCents = catalogPriceCents,
            PhysicalStock = physicalStock,
            ReservedStock = reservedStock,
            AvailableStock = physicalStock - reservedStock,
            IsActive = isActive,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.ProductVariants.Add(variant);
        AddAuditEvent(actorUserId, tenantId, "AdminProducts.Variants.Create", normalizedBaseSku, normalizedVariantSku, new
        {
            variant.BaseSku,
            variant.VariantSku,
            variant.IsActive
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<AdminProductVariantResult>.Success(Map(variant));
    }

    public async Task<ServiceResult<AdminProductVariantResult>> UpdateAsync(
        string baseSku,
        string variantSku,
        AdminProductVariantUpdateRequest request,
        Guid actorUserId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateUpdateRequest(baseSku, variantSku, request, actorUserId);
        if (errors.Count > 0)
        {
            return ServiceResult<AdminProductVariantResult>.Failure(errors);
        }

        var normalizedBaseSku = Sku.Normalize(baseSku);
        var normalizedVariantSku = Sku.Normalize(variantSku);

        var product = await _dbContext.Products.FirstOrDefaultAsync(item => item.Sku == normalizedBaseSku, cancellationToken);
        if (product == null)
        {
            return ServiceResult<AdminProductVariantResult>.Failure(new[]
            {
                new ValidationError("sku", "Product not found")
            });
        }

        var variant = await _dbContext.ProductVariants.FirstOrDefaultAsync(
            item => item.VariantSku == normalizedVariantSku && item.BaseSku == normalizedBaseSku,
            cancellationToken);

        if (variant == null)
        {
            return ServiceResult<AdminProductVariantResult>.Failure(new[]
            {
                new ValidationError("variantSku", "Variant not found")
            });
        }

        var name = request.Name != null
            ? (string.IsNullOrWhiteSpace(request.Name) ? variant.Name : request.Name.Trim())
            : variant.Name;

        var costPriceCents = request.CostPriceCents ?? variant.CostPriceCents;
        var catalogPriceCents = request.CatalogPriceCents ?? variant.CatalogPriceCents;
        var physicalStock = request.PhysicalStock ?? variant.PhysicalStock;
        var reservedStock = request.ReservedStock ?? variant.ReservedStock;
        var isActive = request.IsActive ?? variant.IsActive;

        errors = ValidateVariantFields(name, costPriceCents, catalogPriceCents, physicalStock, reservedStock);
        if (errors.Count > 0)
        {
            return ServiceResult<AdminProductVariantResult>.Failure(errors);
        }

        variant.Name = name;
        variant.CostPriceCents = costPriceCents;
        variant.CatalogPriceCents = catalogPriceCents;
        variant.PhysicalStock = physicalStock;
        variant.ReservedStock = reservedStock;
        variant.AvailableStock = physicalStock - reservedStock;
        variant.IsActive = isActive;
        variant.UpdatedAt = DateTimeOffset.UtcNow;

        AddAuditEvent(actorUserId, tenantId, "AdminProducts.Variants.Update", normalizedBaseSku, normalizedVariantSku, new
        {
            variant.BaseSku,
            variant.VariantSku,
            variant.IsActive
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<AdminProductVariantResult>.Success(Map(variant));
    }

    public async Task<ServiceResult<bool>> DeactivateAsync(
        string baseSku,
        string variantSku,
        Guid actorUserId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<ValidationError>();
        if (!Sku.TryParse(baseSku, out var parsedBaseSku))
        {
            errors.Add(new ValidationError("sku", "SKU format is invalid"));
        }

        if (!Sku.TryParse(variantSku, out var parsedVariantSku))
        {
            errors.Add(new ValidationError("variantSku", "Variant SKU format is invalid"));
        }

        if (actorUserId == Guid.Empty)
        {
            errors.Add(new ValidationError("actor", "Actor user is required"));
        }

        if (errors.Count > 0)
        {
            return ServiceResult<bool>.Failure(errors);
        }

        var variant = await _dbContext.ProductVariants.FirstOrDefaultAsync(
            item => item.BaseSku == parsedBaseSku.Value && item.VariantSku == parsedVariantSku.Value,
            cancellationToken);

        if (variant == null || !variant.IsActive)
        {
            return ServiceResult<bool>.Success(false);
        }

        variant.IsActive = false;
        variant.UpdatedAt = DateTimeOffset.UtcNow;

        AddAuditEvent(actorUserId, tenantId, "AdminProducts.Variants.Deactivate", parsedBaseSku.Value, parsedVariantSku.Value, new
        {
            variant.BaseSku,
            variant.VariantSku
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    private static List<ValidationError> ValidateCreateRequest(string baseSku, AdminProductVariantCreateRequest request, Guid actorUserId)
    {
        var errors = new List<ValidationError>();
        if (!Sku.TryParse(baseSku, out _))
        {
            errors.Add(new ValidationError("sku", "SKU format is invalid"));
        }

        if (request == null)
        {
            errors.Add(new ValidationError("request", "Request is required"));
            return errors;
        }

        if (!Sku.TryParse(request.VariantSku, out _))
        {
            errors.Add(new ValidationError("variantSku", "Variant SKU format is invalid"));
        }

        if (actorUserId == Guid.Empty)
        {
            errors.Add(new ValidationError("actor", "Actor user is required"));
        }

        return errors;
    }

    private static List<ValidationError> ValidateUpdateRequest(string baseSku, string variantSku, AdminProductVariantUpdateRequest request, Guid actorUserId)
    {
        var errors = new List<ValidationError>();

        if (!Sku.TryParse(baseSku, out _))
        {
            errors.Add(new ValidationError("sku", "SKU format is invalid"));
        }

        if (!Sku.TryParse(variantSku, out _))
        {
            errors.Add(new ValidationError("variantSku", "Variant SKU format is invalid"));
        }

        if (request == null)
        {
            errors.Add(new ValidationError("request", "Request is required"));
        }

        if (actorUserId == Guid.Empty)
        {
            errors.Add(new ValidationError("actor", "Actor user is required"));
        }

        return errors;
    }

    private static List<ValidationError> ValidateVariantFields(
        string name,
        long costPriceCents,
        long catalogPriceCents,
        int physicalStock,
        int reservedStock)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add(new ValidationError("name", "Variant name is required"));
        }
        else if (name.Trim().Length > 250)
        {
            errors.Add(new ValidationError("name", "Variant name cannot exceed 250 characters"));
        }

        if (costPriceCents < 0)
        {
            errors.Add(new ValidationError("costPriceCents", "Cost price cannot be negative"));
        }

        if (catalogPriceCents < 0)
        {
            errors.Add(new ValidationError("catalogPriceCents", "Catalog price cannot be negative"));
        }

        if (physicalStock < 0)
        {
            errors.Add(new ValidationError("physicalStock", "Physical stock cannot be negative"));
        }

        if (reservedStock < 0)
        {
            errors.Add(new ValidationError("reservedStock", "Reserved stock cannot be negative"));
        }

        if (reservedStock > physicalStock)
        {
            errors.Add(new ValidationError("reservedStock", "Reserved stock cannot exceed physical stock"));
        }

        return errors;
    }

    private void AddAuditEvent(Guid actorUserId, string tenantId, string action, string baseSku, string variantSku, object metadata)
    {
        _dbContext.AuditEvents.Add(new AuditEvent
        {
            TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
            ActorType = "AdminUser",
            ActorId = actorUserId,
            Action = action,
            Entity = nameof(ProductVariant),
            EntityId = null,
            RequestId = Guid.NewGuid(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                baseSku,
                variantSku,
                metadata
            })
        });
    }

    private static AdminProductVariantResult Map(ProductVariant item)
    {
        return new AdminProductVariantResult
        {
            VariantSku = item.VariantSku,
            BaseSku = item.BaseSku,
            Name = item.Name,
            CostPriceCents = item.CostPriceCents,
            CatalogPriceCents = item.CatalogPriceCents,
            PhysicalStock = item.PhysicalStock,
            ReservedStock = item.ReservedStock,
            AvailableStock = item.AvailableStock,
            IsActive = item.IsActive,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }
}
