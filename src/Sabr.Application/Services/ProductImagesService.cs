using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.ValueObjects;

namespace Phub.Application.Services;

public sealed class ProductImagesService
{
    public const int MaxImagesPerProduct = 10;
    public const long MaxImageSizeBytes = 5 * 1024 * 1024;

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/svg+xml",
        "image/png",
        "image/jpeg"
    };

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".svg",
        ".png",
        ".jpg",
        ".jpeg"
    };

    private readonly IAppDbContext _dbContext;
    private readonly IFileStorage _fileStorage;

    public ProductImagesService(IAppDbContext dbContext, IFileStorage fileStorage)
    {
        _dbContext = dbContext;
        _fileStorage = fileStorage;
    }

    public async Task<ServiceResult<ProductImageResult>> UploadAsync(
        string sku,
        string fileName,
        string contentType,
        Stream content,
        long sizeBytes,
        Guid actorUserId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateUploadRequest(sku, fileName, contentType, sizeBytes, actorUserId);
        if (errors.Count > 0)
        {
            return ServiceResult<ProductImageResult>.Failure(errors);
        }

        var normalizedSku = Sku.Normalize(sku);
        var product = await _dbContext.Products.FirstOrDefaultAsync(item => item.Sku == normalizedSku, cancellationToken);
        if (product == null)
        {
            return ServiceResult<ProductImageResult>.Failure(new[]
            {
                new ValidationError("sku", "Product not found")
            });
        }

        var existing = await _dbContext.ProductImages
            .Where(item => item.ProductSku == normalizedSku)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);

        if (existing.Count >= MaxImagesPerProduct)
        {
            return ServiceResult<ProductImageResult>.Failure(new[]
            {
                new ValidationError("images", "Image limit exceeded")
            });
        }

        var safeFileName = Path.GetFileName(fileName);
        var relativePath = Path.Combine("product-images", normalizedSku, $"{Guid.NewGuid():N}_{safeFileName}");
        var stored = await _fileStorage.SaveAsync(
            new FileStorageRequest(relativePath, content, contentType),
            cancellationToken);

        var maxSort = existing.Count == 0 ? -1 : existing.Max(item => item.SortOrder);
        var isPrimary = existing.Count == 0 || existing.All(item => !item.IsPrimary);
        var image = new ProductImage
        {
            ProductSku = normalizedSku,
            Url = stored.FileUrl,
            MimeType = contentType,
            SizeBytes = stored.SizeBytes,
            SortOrder = maxSort + 1,
            IsPrimary = isPrimary,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.ProductImages.Add(image);
        if (isPrimary || string.IsNullOrWhiteSpace(product.ThumbnailUrl))
        {
            product.ThumbnailUrl = image.Url;
            product.UpdatedAt = DateTimeOffset.UtcNow;
        }

        AddAuditEvent(actorUserId, tenantId, "AdminProducts.UploadImage", normalizedSku, new
        {
            image.Id,
            image.MimeType,
            image.SizeBytes,
            image.SortOrder,
            image.IsPrimary
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<ProductImageResult>.Success(Map(image));
    }

    public async Task<ServiceResult<bool>> DeleteAsync(
        string sku,
        Guid imageId,
        Guid actorUserId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        if (!Sku.TryParse(sku, out var parsedSku))
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("sku", "SKU format is invalid")
            });
        }

        if (actorUserId == Guid.Empty)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("actor", "Actor user is required")
            });
        }

        var image = await _dbContext.ProductImages
            .FirstOrDefaultAsync(item => item.Id == imageId && item.ProductSku == parsedSku.Value, cancellationToken);

        if (image == null)
        {
            return ServiceResult<bool>.Success(false);
        }

        _dbContext.ProductImages.Remove(image);

        var product = await _dbContext.Products.FirstOrDefaultAsync(item => item.Sku == parsedSku.Value, cancellationToken);
        if (product != null && image.IsPrimary)
        {
            var nextPrimary = await _dbContext.ProductImages
                .Where(item => item.ProductSku == parsedSku.Value && item.Id != image.Id)
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (nextPrimary != null)
            {
                nextPrimary.IsPrimary = true;
                product.ThumbnailUrl = nextPrimary.Url;
            }
            else
            {
                product.ThumbnailUrl = null;
            }

            product.UpdatedAt = DateTimeOffset.UtcNow;
        }

        AddAuditEvent(actorUserId, tenantId, "AdminProducts.DeleteImage", parsedSku.Value, new
        {
            imageId
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<ProductImageResult>> SetPrimaryAsync(
        string sku,
        Guid imageId,
        Guid actorUserId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        if (!Sku.TryParse(sku, out var parsedSku))
        {
            return ServiceResult<ProductImageResult>.Failure(new[]
            {
                new ValidationError("sku", "SKU format is invalid")
            });
        }

        if (actorUserId == Guid.Empty)
        {
            return ServiceResult<ProductImageResult>.Failure(new[]
            {
                new ValidationError("actor", "Actor user is required")
            });
        }

        var images = await _dbContext.ProductImages
            .Where(item => item.ProductSku == parsedSku.Value)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);

        var target = images.FirstOrDefault(item => item.Id == imageId);
        if (target == null)
        {
            return ServiceResult<ProductImageResult>.Failure(new[]
            {
                new ValidationError("imageId", "Image not found")
            });
        }

        foreach (var item in images)
        {
            item.IsPrimary = item.Id == imageId;
        }

        var product = await _dbContext.Products.FirstOrDefaultAsync(item => item.Sku == parsedSku.Value, cancellationToken);
        if (product != null)
        {
            product.ThumbnailUrl = target.Url;
            product.UpdatedAt = DateTimeOffset.UtcNow;
        }

        AddAuditEvent(actorUserId, tenantId, "AdminProducts.SetPrimaryImage", parsedSku.Value, new
        {
            imageId
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<ProductImageResult>.Success(Map(target));
    }

    private static List<ValidationError> ValidateUploadRequest(
        string sku,
        string fileName,
        string contentType,
        long sizeBytes,
        Guid actorUserId)
    {
        var errors = new List<ValidationError>();

        if (!Sku.TryParse(sku, out _))
        {
            errors.Add(new ValidationError("sku", "SKU format is invalid"));
        }

        if (actorUserId == Guid.Empty)
        {
            errors.Add(new ValidationError("actor", "Actor user is required"));
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            errors.Add(new ValidationError("file", "File name is required"));
        }
        else
        {
            var extension = Path.GetExtension(fileName);
            if (!AllowedExtensions.Contains(extension))
            {
                errors.Add(new ValidationError("fileType", "Invalid image extension"));
            }
        }

        if (string.IsNullOrWhiteSpace(contentType) || !AllowedContentTypes.Contains(contentType))
        {
            errors.Add(new ValidationError("fileType", "Invalid image MIME type"));
        }

        if (sizeBytes <= 0)
        {
            errors.Add(new ValidationError("fileSize", "Image file is empty"));
        }
        else if (sizeBytes > MaxImageSizeBytes)
        {
            errors.Add(new ValidationError("fileSize", $"Image size exceeds {MaxImageSizeBytes} bytes"));
        }

        return errors;
    }

    private void AddAuditEvent(Guid actorUserId, string tenantId, string action, string sku, object metadata)
    {
        _dbContext.AuditEvents.Add(new AuditEvent
        {
            TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
            ActorType = "AdminUser",
            ActorId = actorUserId,
            Action = action,
            Entity = nameof(ProductImage),
            EntityId = null,
            RequestId = Guid.NewGuid(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                sku,
                metadata
            })
        });
    }

    private static ProductImageResult Map(ProductImage image)
    {
        return new ProductImageResult
        {
            Id = image.Id,
            ProductSku = image.ProductSku,
            Url = image.Url,
            MimeType = image.MimeType,
            SizeBytes = image.SizeBytes,
            SortOrder = image.SortOrder,
            IsPrimary = image.IsPrimary,
            CreatedAt = image.CreatedAt
        };
    }
}
