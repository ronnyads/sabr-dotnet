using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
using Phub.Domain.Entities;

namespace Phub.Application.Services;

public sealed class AdminCategoryService
{
    private static readonly Regex CategorySlugRegex = new("^[a-z0-9][a-z0-9_/-]{0,119}$", RegexOptions.Compiled);
    private static readonly TimeSpan TreeCacheTtl   = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ItemCacheTtl   = TimeSpan.FromMinutes(5);

    private readonly IAppDbContext _dbContext;
    private readonly ICacheService _cache;

    public AdminCategoryService(IAppDbContext dbContext, ICacheService cache)
    {
        _dbContext = dbContext;
        _cache     = cache;
    }

    public async Task<ServiceResult<PagedResult<AdminCategoryResult>>> ListAsync(
        int skip,
        int limit,
        string? search,
        bool? isActive,
        CancellationToken cancellationToken = default)
    {
        var errors = PaginationGuard.ValidateOrError(skip, limit);
        if (errors.Count > 0)
        {
            return ServiceResult<PagedResult<AdminCategoryResult>>.Failure(errors);
        }

        var query = _dbContext.Categories.AsNoTracking().AsQueryable();
        if (isActive.HasValue)
        {
            query = query.Where(item => item.IsActive == isActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            query = query.Where(item =>
                item.Name.ToUpper().Contains(term) ||
                item.Slug.ToUpper().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(item => item.Name)
            .ThenBy(item => item.Slug)
            .Skip(skip)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var parentIds = items.Where(item => item.ParentId.HasValue).Select(item => item.ParentId!.Value).Distinct().ToList();
        var parentMap = await _dbContext.Categories
            .AsNoTracking()
            .Where(item => parentIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, item => item.Slug, cancellationToken);

        return ServiceResult<PagedResult<AdminCategoryResult>>.Success(new PagedResult<AdminCategoryResult>
        {
            Items = items.Select(item => new AdminCategoryResult
            {
                Id = item.Id,
                Name = item.Name,
                Slug = item.Slug,
                ParentId = item.ParentId,
                ParentSlug = item.ParentId.HasValue && parentMap.TryGetValue(item.ParentId.Value, out var parentSlug)
                    ? parentSlug
                    : null,
                Icon = item.Icon,
                Description = item.Description,
                IsActive = item.IsActive,
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt
            }).ToList(),
            Total = total,
            Skip = skip,
            Limit = limit
        });
    }

    public async Task<ServiceResult<IReadOnlyCollection<AdminCategoryTreeNodeResult>>> TreeAsync(CancellationToken cancellationToken = default)
    {
        return await _cache.GetOrSetAsync(
            CacheKeys.CategoryTree,
            async ct =>
            {
                var inner = await BuildTreeAsync(ct);
                return inner.Data!;
            },
            TreeCacheTtl,
            cancellationToken) is { } cached
            ? ServiceResult<IReadOnlyCollection<AdminCategoryTreeNodeResult>>.Success(cached)
            : await BuildTreeAsync(cancellationToken);
    }

    private async Task<ServiceResult<IReadOnlyCollection<AdminCategoryTreeNodeResult>>> BuildTreeAsync(CancellationToken cancellationToken)
    {
        var categories = await _dbContext.Categories
            .AsNoTracking()
            .OrderBy(item => item.Name)
            .ThenBy(item => item.Slug)
            .ToListAsync(cancellationToken);

        var nodes = categories.ToDictionary(
            item => item.Id,
            item => new MutableCategoryNode
            {
                Id = item.Id,
                Name = item.Name,
                Slug = item.Slug,
                ParentId = item.ParentId,
                IsActive = item.IsActive,
                Path = item.Name
            });

        var roots = new List<MutableCategoryNode>();
        foreach (var category in categories)
        {
            var node = nodes[category.Id];
            if (category.ParentId.HasValue && nodes.TryGetValue(category.ParentId.Value, out var parentNode))
            {
                parentNode.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        foreach (var root in roots)
        {
            PopulatePath(root, null);
        }

        var orderedRoots = roots
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Slug, StringComparer.OrdinalIgnoreCase)
            .Select(ToResult)
            .ToList();

        return ServiceResult<IReadOnlyCollection<AdminCategoryTreeNodeResult>>.Success(orderedRoots);
    }

    public async Task<ServiceResult<AdminCategoryDetailResult>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var category = await _dbContext.Categories
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (category == null)
        {
            return ServiceResult<AdminCategoryDetailResult>.Failure(new[]
            {
                new ValidationError("categoryId", "Category not found")
            });
        }

        var path = await BuildCategoryPathAsync(category, cancellationToken);

        string? parentSlug = null;
        if (category.ParentId.HasValue)
        {
            parentSlug = await _dbContext.Categories
                .AsNoTracking()
                .Where(item => item.Id == category.ParentId.Value)
                .Select(item => item.Slug)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return ServiceResult<AdminCategoryDetailResult>.Success(new AdminCategoryDetailResult
        {
            Id = category.Id,
            Name = category.Name,
            Slug = category.Slug,
            ParentId = category.ParentId,
            ParentSlug = parentSlug,
            Icon = category.Icon,
            Description = category.Description,
            IsActive = category.IsActive,
            Path = path,
            CreatedAt = category.CreatedAt,
            UpdatedAt = category.UpdatedAt
        });
    }

    public async Task<ServiceResult<AdminCategoryDetailResult>> CreateAsync(
        AdminCategoryUpsertRequest request,
        Guid actorUserId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var errors = await ValidateCategoryRequestAsync(request, actorUserId, null, cancellationToken);
        if (errors.Count > 0)
        {
            return ServiceResult<AdminCategoryDetailResult>.Failure(errors);
        }

        var category = new Category
        {
            Name = request.Name.Trim(),
            Slug = NormalizeSlug(request.Slug),
            ParentId = request.ParentId,
            Icon = NormalizeOptional(request.Icon),
            Description = NormalizeOptional(request.Description),
            IsActive = request.IsActive,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.Categories.Add(category);
        AddAuditEvent(actorUserId, tenantId, "AdminCategories.Create", category.Id, new
        {
            category.Slug,
            category.ParentId,
            category.IsActive
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _cache.RemoveAsync(CacheKeys.CategoryTree, cancellationToken);
        return await GetByIdAsync(category.Id, cancellationToken);
    }

    public async Task<ServiceResult<AdminCategoryDetailResult>> UpdateAsync(
        Guid categoryId,
        AdminCategoryUpsertRequest request,
        Guid actorUserId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var category = await _dbContext.Categories.FirstOrDefaultAsync(item => item.Id == categoryId, cancellationToken);
        if (category == null)
        {
            return ServiceResult<AdminCategoryDetailResult>.Failure(new[]
            {
                new ValidationError("categoryId", "Category not found")
            });
        }

        var errors = await ValidateCategoryRequestAsync(request, actorUserId, categoryId, cancellationToken);
        if (errors.Count > 0)
        {
            return ServiceResult<AdminCategoryDetailResult>.Failure(errors);
        }

        category.Name = request.Name.Trim();
        category.Slug = NormalizeSlug(request.Slug);
        category.ParentId = request.ParentId;
        category.Icon = NormalizeOptional(request.Icon);
        category.Description = NormalizeOptional(request.Description);
        category.IsActive = request.IsActive;
        category.UpdatedAt = DateTimeOffset.UtcNow;

        AddAuditEvent(actorUserId, tenantId, "AdminCategories.Update", category.Id, new
        {
            category.Slug,
            category.ParentId,
            category.IsActive
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await Task.WhenAll(
            _cache.RemoveAsync(CacheKeys.CategoryTree, cancellationToken),
            _cache.RemoveAsync(CacheKeys.CategoryById(categoryId), cancellationToken));
        return await GetByIdAsync(category.Id, cancellationToken);
    }

    public async Task<ServiceResult<bool>> DeactivateAsync(
        Guid categoryId,
        Guid actorUserId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        if (actorUserId == Guid.Empty)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("actor", "Actor user is required")
            });
        }

        var category = await _dbContext.Categories.FirstOrDefaultAsync(item => item.Id == categoryId, cancellationToken);
        if (category == null || !category.IsActive)
        {
            return ServiceResult<bool>.Success(false);
        }

        var hasActiveChildren = await _dbContext.Categories
            .AsNoTracking()
            .AnyAsync(item => item.ParentId == categoryId && item.IsActive, cancellationToken);

        if (hasActiveChildren)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("children", "Category has active children")
            });
        }

        category.IsActive = false;
        category.UpdatedAt = DateTimeOffset.UtcNow;

        AddAuditEvent(actorUserId, tenantId, "AdminCategories.Deactivate", category.Id, new
        {
            category.Slug
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await Task.WhenAll(
            _cache.RemoveAsync(CacheKeys.CategoryTree, cancellationToken),
            _cache.RemoveAsync(CacheKeys.CategoryById(categoryId), cancellationToken));
        return ServiceResult<bool>.Success(true);
    }

    private async Task<List<ValidationError>> ValidateCategoryRequestAsync(
        AdminCategoryUpsertRequest request,
        Guid actorUserId,
        Guid? currentCategoryId,
        CancellationToken cancellationToken)
    {
        var errors = new List<ValidationError>();
        if (request == null)
        {
            errors.Add(new ValidationError("request", "Request is required"));
            return errors;
        }

        if (actorUserId == Guid.Empty)
        {
            errors.Add(new ValidationError("actor", "Actor user is required"));
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors.Add(new ValidationError("name", "Name is required"));
        }
        else if (request.Name.Trim().Length > 200)
        {
            errors.Add(new ValidationError("name", "Name cannot exceed 200 characters"));
        }

        if (string.IsNullOrWhiteSpace(request.Slug))
        {
            errors.Add(new ValidationError("slug", "Slug is required"));
        }
        else
        {
            var normalizedSlug = NormalizeSlug(request.Slug);
            if (!CategorySlugRegex.IsMatch(normalizedSlug))
            {
                errors.Add(new ValidationError("slug", "Slug format is invalid"));
            }
            else
            {
                var duplicate = await _dbContext.Categories.AnyAsync(
                    item => item.Slug == normalizedSlug && (!currentCategoryId.HasValue || item.Id != currentCategoryId.Value),
                    cancellationToken);

                if (duplicate)
                {
                    errors.Add(new ValidationError("slug", "Category slug already exists"));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Icon) && request.Icon.Trim().Length > 120)
        {
            errors.Add(new ValidationError("icon", "Icon cannot exceed 120 characters"));
        }

        if (!string.IsNullOrWhiteSpace(request.Description) && request.Description.Trim().Length > 600)
        {
            errors.Add(new ValidationError("description", "Description cannot exceed 600 characters"));
        }

        if (request.ParentId.HasValue)
        {
            if (currentCategoryId.HasValue && request.ParentId.Value == currentCategoryId.Value)
            {
                errors.Add(new ValidationError("parentId", "Category cannot be its own parent"));
            }
            else
            {
                var parent = await _dbContext.Categories
                    .AsNoTracking()
                    .FirstOrDefaultAsync(item => item.Id == request.ParentId.Value, cancellationToken);

                if (parent == null)
                {
                    errors.Add(new ValidationError("parentId", "Parent category not found"));
                }
                else
                {
                    if (request.IsActive && !parent.IsActive)
                    {
                        errors.Add(new ValidationError("parentId", "Cannot activate category under inactive parent"));
                    }

                    if (currentCategoryId.HasValue)
                    {
                        var causesCycle = await IsDescendantAsync(request.ParentId.Value, currentCategoryId.Value, cancellationToken);
                        if (causesCycle)
                        {
                            errors.Add(new ValidationError("parentId", "Category tree cycle detected"));
                        }
                    }
                }
            }
        }

        return errors;
    }

    private async Task<bool> IsDescendantAsync(Guid possibleParentId, Guid categoryId, CancellationToken cancellationToken)
    {
        var visited = new HashSet<Guid>();
        var cursor = possibleParentId;

        while (true)
        {
            if (!visited.Add(cursor))
            {
                return true;
            }

            if (cursor == categoryId)
            {
                return true;
            }

            var parentId = await _dbContext.Categories
                .AsNoTracking()
                .Where(item => item.Id == cursor)
                .Select(item => item.ParentId)
                .FirstOrDefaultAsync(cancellationToken);

            if (!parentId.HasValue)
            {
                return false;
            }

            cursor = parentId.Value;
        }
    }

    private async Task<string> BuildCategoryPathAsync(Category category, CancellationToken cancellationToken)
    {
        var visited = new HashSet<Guid>();
        var names = new List<string>();
        var cursor = category;

        while (cursor != null)
        {
            if (!visited.Add(cursor.Id))
            {
                break;
            }

            names.Add(cursor.Name);
            if (!cursor.ParentId.HasValue)
            {
                break;
            }

            var parent = await _dbContext.Categories
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == cursor.ParentId.Value, cancellationToken);

            if (parent == null)
            {
                break;
            }

            cursor = parent;
        }

        names.Reverse();
        return string.Join(" / ", names);
    }

    private static void PopulatePath(MutableCategoryNode node, string? parentPath)
    {
        node.Path = string.IsNullOrWhiteSpace(parentPath) ? node.Name : $"{parentPath} / {node.Name}";

        foreach (var child in node.Children
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Slug, StringComparer.OrdinalIgnoreCase))
        {
            PopulatePath(child, node.Path);
        }
    }

    private static AdminCategoryTreeNodeResult ToResult(MutableCategoryNode node)
    {
        return new AdminCategoryTreeNodeResult
        {
            Id = node.Id,
            Name = node.Name,
            Slug = node.Slug,
            ParentId = node.ParentId,
            IsActive = node.IsActive,
            Path = node.Path,
            Children = node.Children
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Slug, StringComparer.OrdinalIgnoreCase)
                .Select(ToResult)
                .ToList()
        };
    }

    private static string NormalizeSlug(string slug)
    {
        return string.IsNullOrWhiteSpace(slug)
            ? string.Empty
            : slug.Trim().ToLowerInvariant();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private void AddAuditEvent(Guid actorUserId, string tenantId, string action, Guid categoryId, object metadata)
    {
        _dbContext.AuditEvents.Add(new AuditEvent
        {
            TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
            ActorType = "AdminUser",
            ActorId = actorUserId,
            Action = action,
            Entity = nameof(Category),
            EntityId = categoryId,
            RequestId = Guid.NewGuid(),
            MetadataJson = JsonSerializer.Serialize(metadata)
        });
    }

    private sealed class MutableCategoryNode
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public Guid? ParentId { get; set; }
        public bool IsActive { get; set; }
        public string Path { get; set; } = string.Empty;
        public List<MutableCategoryNode> Children { get; } = new();
    }
}
