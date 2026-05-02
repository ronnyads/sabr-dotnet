using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Validation;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;
using Sabr.Domain.ValueObjects;

namespace Sabr.Application.Services;

public sealed class AdminCatalogService
{
    private readonly IAppDbContext _dbContext;

    public AdminCatalogService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Global (no tenant) — catalogs are platform-level resources
    // ──────────────────────────────────────────────────────────────────────

    public async Task<PagedResult<AdminCatalogResult>> ListAsync(
        int skip,
        int limit,
        string? search,
        bool? isActive,
        CancellationToken cancellationToken = default)
    {
        var errors = PaginationGuard.ValidateOrError(skip, limit);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Invalid pagination: {string.Join(", ", errors.Select(e => e.Message))}");
        }

        var query = _dbContext.Catalogs.AsNoTracking();

        if (isActive.HasValue)
        {
            query = query.Where(item => item.IsActive == isActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            query = query.Where(item =>
                item.Name.ToUpper().Contains(term) ||
                (item.Description != null && item.Description.ToUpper().Contains(term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.Name)
            .Skip(skip)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var catalogIds = items.Select(item => item.Id).ToList();

        var productCounts = await _dbContext.ProductCatalogs
            .AsNoTracking()
            .Where(item => catalogIds.Contains(item.CatalogId))
            .GroupBy(item => item.CatalogId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken);

        var planCounts = await _dbContext.PlanCatalogs
            .AsNoTracking()
            .Where(item => catalogIds.Contains(item.CatalogId))
            .GroupBy(item => item.CatalogId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken);

        var results = items.Select(item =>
        {
            productCounts.TryGetValue(item.Id, out var productCount);
            planCounts.TryGetValue(item.Id, out var planCount);
            return new AdminCatalogResult
            {
                Id = item.Id,
                Name = item.Name,
                Description = item.Description,
                IsActive = item.IsActive,
                ProductCount = productCount,
                PlanCount = planCount,
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt
            };
        }).ToList();

        return new PagedResult<AdminCatalogResult>
        {
            Items = results,
            Total = total,
            Skip = skip,
            Limit = limit
        };
    }

    // Keep for backward compatibility — tenant-scoped now returns global list
    public async Task<ServiceResult<PagedResult<AdminCatalogResult>>> ListAsync(
        string tenantSlug,
        int skip,
        int limit,
        string? search,
        bool? isActive,
        CancellationToken cancellationToken = default)
    {
        var errors = PaginationGuard.ValidateOrError(skip, limit);
        if (errors.Count > 0)
        {
            return ServiceResult<PagedResult<AdminCatalogResult>>.Failure(errors);
        }

        var result = await ListAsync(skip, limit, search, isActive, cancellationToken);
        return ServiceResult<PagedResult<AdminCatalogResult>>.Success(result);
    }

    /// <summary>
    /// Lista catálogos da plataforma (view global para SuperAdmin).
    /// </summary>
    public async Task<PagedResult<AdminCatalogGlobalResult>> ListGlobalAsync(
        int skip,
        int limit,
        string? search,
        bool? isActive,
        CancellationToken cancellationToken = default)
    {
        var errors = PaginationGuard.ValidateOrError(skip, limit);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Invalid pagination: {string.Join(", ", errors.Select(e => e.Message))}");
        }

        var query = _dbContext.Catalogs.AsNoTracking();

        if (isActive.HasValue)
        {
            query = query.Where(x => x.IsActive == isActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            query = query.Where(x =>
                x.Name.ToUpper().Contains(term) ||
                (x.Description != null && x.Description.ToUpper().Contains(term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.Name)
            .Skip(skip)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var catalogIds = items.Select(x => x.Id).ToList();

        var productCounts = await _dbContext.ProductCatalogs
            .AsNoTracking()
            .Where(item => catalogIds.Contains(item.CatalogId))
            .GroupBy(item => item.CatalogId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken);

        var planCounts = await _dbContext.PlanCatalogs
            .AsNoTracking()
            .Where(item => catalogIds.Contains(item.CatalogId))
            .GroupBy(item => item.CatalogId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken);

        var results = items.Select(x =>
        {
            productCounts.TryGetValue(x.Id, out var productCount);
            planCounts.TryGetValue(x.Id, out var planCount);
            return new AdminCatalogGlobalResult
            {
                Id = x.Id,
                Name = x.Name,
                Description = x.Description,
                IsActive = x.IsActive,
                ProductCount = productCount,
                PlanCount = planCount,
                UpdatedAt = x.UpdatedAt
            };
        }).ToList();

        return new PagedResult<AdminCatalogGlobalResult>
        {
            Items = results,
            Total = total,
            Skip = skip,
            Limit = limit
        };
    }

    public async Task<ServiceResult<AdminCatalogDetailResult>> GetByIdAsync(
        Guid catalogId,
        CancellationToken cancellationToken = default)
    {
        return await GetByIdInternalAsync(catalogId, cancellationToken);
    }

    // Backward compat overload (tenant param ignored)
    public async Task<ServiceResult<AdminCatalogDetailResult>> GetByIdAsync(
        string tenantSlug,
        Guid catalogId,
        CancellationToken cancellationToken = default)
    {
        return await GetByIdInternalAsync(catalogId, cancellationToken);
    }

    public async Task<ServiceResult<AdminCatalogDetailResult>> CreateAsync(
        AdminCatalogUpsertRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateCatalogRequest(request, actorUserId);
        if (errors.Count > 0)
        {
            return ServiceResult<AdminCatalogDetailResult>.Failure(errors);
        }

        var normalizedName = request.Name.Trim();
        var duplicateName = await _dbContext.Catalogs.AnyAsync(
            item => item.Name == normalizedName,
            cancellationToken);

        if (duplicateName)
        {
            return ServiceResult<AdminCatalogDetailResult>.Failure(new[]
            {
                new ValidationError("name", "Catalog name already exists")
            });
        }

        var catalog = new Catalog
        {
            Name = normalizedName,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            IsActive = request.IsActive,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.Catalogs.Add(catalog);
        AddAuditEvent(
            string.Empty,
            actorUserId,
            "AdminCatalogs.Create",
            nameof(Catalog),
            catalog.Id,
            new { catalog.Name, catalog.IsActive });
        await _dbContext.SaveChangesAsync(cancellationToken);

        return await GetByIdInternalAsync(catalog.Id, cancellationToken);
    }

    // Backward compat overload (tenantSlug param used only for audit)
    public async Task<ServiceResult<AdminCatalogDetailResult>> CreateAsync(
        string tenantSlug,
        AdminCatalogUpsertRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        return await CreateAsync(request, actorUserId, cancellationToken);
    }

    public async Task<ServiceResult<AdminCatalogDetailResult>> UpdateAsync(
        Guid catalogId,
        AdminCatalogUpsertRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateCatalogRequest(request, actorUserId);
        if (errors.Count > 0)
        {
            return ServiceResult<AdminCatalogDetailResult>.Failure(errors);
        }

        var catalog = await _dbContext.Catalogs.FirstOrDefaultAsync(
            item => item.Id == catalogId,
            cancellationToken);

        if (catalog == null)
        {
            return ServiceResult<AdminCatalogDetailResult>.Failure(new[]
            {
                new ValidationError("catalogId", "Catalog not found")
            });
        }

        var normalizedName = request.Name.Trim();
        var duplicateName = await _dbContext.Catalogs.AnyAsync(
            item => item.Id != catalogId && item.Name == normalizedName,
            cancellationToken);

        if (duplicateName)
        {
            return ServiceResult<AdminCatalogDetailResult>.Failure(new[]
            {
                new ValidationError("name", "Catalog name already exists")
            });
        }

        catalog.Name = normalizedName;
        catalog.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        catalog.IsActive = request.IsActive;
        catalog.UpdatedAt = DateTimeOffset.UtcNow;

        AddAuditEvent(
            string.Empty,
            actorUserId,
            "AdminCatalogs.Update",
            nameof(Catalog),
            catalog.Id,
            new { catalog.Name, catalog.IsActive });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return await GetByIdInternalAsync(catalog.Id, cancellationToken);
    }

    // Backward compat overload (tenantSlug param ignored)
    public async Task<ServiceResult<AdminCatalogDetailResult>> UpdateAsync(
        string tenantSlug,
        Guid catalogId,
        AdminCatalogUpsertRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        return await UpdateAsync(catalogId, request, actorUserId, cancellationToken);
    }

    public async Task<ServiceResult<bool>> DeactivateAsync(
        Guid catalogId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (actorUserId == Guid.Empty)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("actor", "Actor user is required")
            });
        }

        var catalog = await _dbContext.Catalogs.FirstOrDefaultAsync(
            item => item.Id == catalogId,
            cancellationToken);

        if (catalog == null || !catalog.IsActive)
        {
            return ServiceResult<bool>.Success(false);
        }

        catalog.IsActive = false;
        catalog.UpdatedAt = DateTimeOffset.UtcNow;
        AddAuditEvent(
            string.Empty,
            actorUserId,
            "AdminCatalogs.Deactivate",
            nameof(Catalog),
            catalog.Id,
            new { catalog.Id });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    // Backward compat overload (tenantSlug param ignored)
    public async Task<ServiceResult<bool>> DeactivateAsync(
        string tenantSlug,
        Guid catalogId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        return await DeactivateAsync(catalogId, actorUserId, cancellationToken);
    }

    public async Task<ServiceResult<AdminCatalogDetailResult>> ReplaceProductsAsync(
        Guid catalogId,
        CatalogReplaceProductsRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (actorUserId == Guid.Empty)
        {
            return ServiceResult<AdminCatalogDetailResult>.Failure(new[]
            {
                new ValidationError("actor", "Actor user is required")
            });
        }

        var catalog = await _dbContext.Catalogs
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == catalogId, cancellationToken);

        if (catalog == null)
        {
            return ServiceResult<AdminCatalogDetailResult>.Failure(new[]
            {
                new ValidationError("catalogId", "Catalog not found")
            });
        }

        request ??= new CatalogReplaceProductsRequest();
        var normalizedSkus = new List<string>();
        var invalidFormatSkus = new List<string>();

        foreach (var rawSku in request.ProductSkus ?? new List<string>())
        {
            if (!Sku.TryParse(rawSku, out var parsedSku))
            {
                invalidFormatSkus.Add(rawSku);
                continue;
            }

            normalizedSkus.Add(parsedSku.Value);
        }

        var desiredSkus = normalizedSkus.Distinct(StringComparer.Ordinal).ToList();
        if (invalidFormatSkus.Count > 0)
        {
            var formatErrors = invalidFormatSkus
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => new ValidationError("invalidSkus", item.Trim().ToUpperInvariant()))
                .ToList();
            formatErrors.Add(new ValidationError("productSkus", "One or more product SKUs are invalid"));

            return ServiceResult<AdminCatalogDetailResult>.Failure(formatErrors);
        }

        var activeProducts = await _dbContext.Products
            .AsNoTracking()
            .Where(item => desiredSkus.Contains(item.Sku) && item.IsActive)
            .Select(item => item.Sku)
            .ToListAsync(cancellationToken);

        var invalidSkus = desiredSkus
            .Except(activeProducts, StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();

        if (invalidSkus.Count > 0)
        {
            var invalidSkuErrors = invalidSkus
                .Select(item => new ValidationError("invalidSkus", item))
                .ToList();
            invalidSkuErrors.Add(new ValidationError("productSkus", "One or more product SKUs are invalid or inactive"));

            return ServiceResult<AdminCatalogDetailResult>.Failure(invalidSkuErrors);
        }

        var efDbContext = (DbContext)_dbContext;
        await using var transaction = await BeginTransactionIfSupportedAsync(efDbContext, cancellationToken);

        var currentRelations = await _dbContext.ProductCatalogs
            .Where(item => item.CatalogId == catalogId)
            .ToListAsync(cancellationToken);

        var currentSkuSet = currentRelations.Select(item => item.ProductSku).ToHashSet(StringComparer.Ordinal);
        var desiredSkuSet = desiredSkus.ToHashSet(StringComparer.Ordinal);

        var toRemove = currentRelations
            .Where(item => !desiredSkuSet.Contains(item.ProductSku))
            .ToList();

        var toAdd = desiredSkus
            .Where(item => !currentSkuSet.Contains(item))
            .ToList();

        if (toRemove.Count > 0)
        {
            _dbContext.ProductCatalogs.RemoveRange(toRemove);
        }

        if (toAdd.Count > 0)
        {
            _dbContext.ProductCatalogs.AddRange(toAdd.Select(item => new ProductCatalog
            {
                CatalogId = catalogId,
                ProductSku = item,
                CreatedAt = DateTimeOffset.UtcNow
            }));
        }

        AddAuditEvent(
            string.Empty,
            actorUserId,
            "AdminCatalogs.ReplaceProducts",
            nameof(Catalog),
            catalogId,
            new
            {
                Added = toAdd.Count,
                Removed = toRemove.Count
            });

        await _dbContext.SaveChangesAsync(cancellationToken);
        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return await GetByIdInternalAsync(catalogId, cancellationToken);
    }

    // Backward compat overload (tenantSlug param ignored)
    public async Task<ServiceResult<AdminCatalogDetailResult>> ReplaceProductsAsync(
        string tenantSlug,
        Guid catalogId,
        CatalogReplaceProductsRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        return await ReplaceProductsAsync(catalogId, request, actorUserId, cancellationToken);
    }

    public async Task<ServiceResult<AdminCatalogDetailResult>> ReplacePlansAsync(
        string tenantSlug,
        Guid catalogId,
        CatalogReplacePlansRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var tenantResult = await ResolveTenantAsync(tenantSlug, cancellationToken);
        if (!tenantResult.Succeeded || tenantResult.Data == null)
        {
            return ServiceResult<AdminCatalogDetailResult>.Failure(tenantResult.Errors);
        }

        if (actorUserId == Guid.Empty)
        {
            return ServiceResult<AdminCatalogDetailResult>.Failure(new[]
            {
                new ValidationError("actor", "Actor user is required")
            });
        }

        var tenant = tenantResult.Data;
        var catalog = await _dbContext.Catalogs
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == catalogId, cancellationToken);

        if (catalog == null)
        {
            return ServiceResult<AdminCatalogDetailResult>.Failure(new[]
            {
                new ValidationError("catalogId", "Catalog not found")
            });
        }

        request ??= new CatalogReplacePlansRequest();
        var desiredPlanIds = (request.PlanIds ?? new List<Guid>())
            .Where(item => item != Guid.Empty)
            .Distinct()
            .ToList();

        var validPlanIds = await _dbContext.Plans
            .AsNoTracking()
            .Where(item => desiredPlanIds.Contains(item.Id) && item.IsActive)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);

        var invalidPlanIds = desiredPlanIds
            .Except(validPlanIds)
            .ToList();

        if (invalidPlanIds.Count > 0)
        {
            var invalidPlanErrors = invalidPlanIds
                .Select(item => new ValidationError("invalidPlanIds", item.ToString()))
                .ToList();
            invalidPlanErrors.Add(new ValidationError("planIds", "One or more plan ids are invalid or inactive"));

            return ServiceResult<AdminCatalogDetailResult>.Failure(invalidPlanErrors);
        }

        var efDbContext = (DbContext)_dbContext;
        await using var transaction = await BeginTransactionIfSupportedAsync(efDbContext, cancellationToken);

        var currentRelations = await _dbContext.PlanCatalogs
            .Where(item => item.CatalogId == catalogId)
            .ToListAsync(cancellationToken);

        var currentSet = currentRelations.Select(item => item.PlanId).ToHashSet();
        var desiredSet = desiredPlanIds.ToHashSet();

        var toRemove = currentRelations
            .Where(item => !desiredSet.Contains(item.PlanId))
            .ToList();

        var toAdd = desiredPlanIds
            .Where(item => !currentSet.Contains(item))
            .ToList();

        if (toRemove.Count > 0)
        {
            _dbContext.PlanCatalogs.RemoveRange(toRemove);
        }

        if (toAdd.Count > 0)
        {
            _dbContext.PlanCatalogs.AddRange(toAdd.Select(item => new PlanCatalog
            {
                CatalogId = catalogId,
                PlanId = item,
                CreatedAt = DateTimeOffset.UtcNow
            }));
        }

        AddAuditEvent(
            tenant.Id,
            actorUserId,
            "AdminCatalogs.ReplacePlans",
            nameof(Catalog),
            catalogId,
            new
            {
                Added = toAdd.Count,
                Removed = toRemove.Count
            });

        await _dbContext.SaveChangesAsync(cancellationToken);
        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return await GetByIdInternalAsync(catalogId, cancellationToken);
    }

    private async Task<ServiceResult<AdminCatalogDetailResult>> GetByIdInternalAsync(
        Guid catalogId,
        CancellationToken cancellationToken)
    {
        var catalog = await _dbContext.Catalogs
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == catalogId, cancellationToken);

        if (catalog == null)
        {
            return ServiceResult<AdminCatalogDetailResult>.Failure(new[]
            {
                new ValidationError("catalogId", "Catalog not found")
            });
        }

        var productSkus = await _dbContext.ProductCatalogs
            .AsNoTracking()
            .Where(item => item.CatalogId == catalog.Id)
            .OrderBy(item => item.ProductSku)
            .Select(item => item.ProductSku)
            .ToListAsync(cancellationToken);

        var planIds = await _dbContext.PlanCatalogs
            .AsNoTracking()
            .Where(item => item.CatalogId == catalog.Id)
            .OrderBy(item => item.PlanId)
            .Select(item => item.PlanId)
            .ToListAsync(cancellationToken);

        return ServiceResult<AdminCatalogDetailResult>.Success(new AdminCatalogDetailResult
        {
            Id = catalog.Id,
            Name = catalog.Name,
            Description = catalog.Description,
            IsActive = catalog.IsActive,
            ProductSkus = productSkus,
            PlanIds = planIds,
            CreatedAt = catalog.CreatedAt,
            UpdatedAt = catalog.UpdatedAt
        });
    }

    private async Task<ServiceResult<Tenant>> ResolveTenantAsync(string tenantSlug, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantSlug))
        {
            return ServiceResult<Tenant>.Failure(new[]
            {
                new ValidationError("tenantId", "Tenant slug is required")
            });
        }

        var normalizedSlug = tenantSlug.Trim().ToLowerInvariant();
        var tenant = await _dbContext.Tenants.FirstOrDefaultAsync(
            item => item.Slug == normalizedSlug,
            cancellationToken);

        if (tenant == null)
        {
            return ServiceResult<Tenant>.Failure(new[]
            {
                new ValidationError("tenantId", "Tenant not found")
            });
        }

        if (tenant.Status != TenantStatus.Active)
        {
            return ServiceResult<Tenant>.Failure(new[]
            {
                new ValidationError("tenantStatus", "Tenant inactive")
            });
        }

        return ServiceResult<Tenant>.Success(tenant);
    }

    private static List<ValidationError> ValidateCatalogRequest(AdminCatalogUpsertRequest request, Guid actorUserId)
    {
        var errors = new List<ValidationError>();

        if (request == null)
        {
            errors.Add(new ValidationError("request", "Request is required"));
            return errors;
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors.Add(new ValidationError("name", "Name is required"));
        }
        else if (request.Name.Trim().Length > 200)
        {
            errors.Add(new ValidationError("name", "Name cannot exceed 200 characters"));
        }

        if (!string.IsNullOrWhiteSpace(request.Description) && request.Description.Trim().Length > 600)
        {
            errors.Add(new ValidationError("description", "Description cannot exceed 600 characters"));
        }

        if (actorUserId == Guid.Empty)
        {
            errors.Add(new ValidationError("actor", "Actor user is required"));
        }

        return errors;
    }

    private void AddAuditEvent(
        string tenantId,
        Guid actorUserId,
        string action,
        string entity,
        Guid entityId,
        object metadata)
    {
        _dbContext.AuditEvents.Add(new AuditEvent
        {
            TenantId = tenantId,
            ActorType = "AdminUser",
            ActorId = actorUserId,
            Action = action,
            Entity = entity,
            EntityId = entityId,
            RequestId = Guid.NewGuid(),
            MetadataJson = JsonSerializer.Serialize(metadata)
        });
    }

    private static async Task<IDbContextTransaction?> BeginTransactionIfSupportedAsync(
        DbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            return null;
        }

        return await dbContext.Database.BeginTransactionAsync(cancellationToken);
    }
}
