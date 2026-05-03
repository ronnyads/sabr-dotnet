using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class AdminPlanService
{
    private readonly IAppDbContext _dbContext;

    public AdminPlanService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ServiceResult<PagedResult<AdminPlanResult>>> ListAsync(
        int skip,
        int limit,
        string? search,
        bool? isActive,
        CancellationToken cancellationToken = default)
    {
        var errors = PaginationGuard.ValidateOrError(skip, limit);
        if (errors.Count > 0)
            return ServiceResult<PagedResult<AdminPlanResult>>.Failure(errors);

        var query = _dbContext.Plans.AsNoTracking();

        if (isActive.HasValue)
            query = query.Where(item => item.IsActive == isActive.Value);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            query = query.Where(item => item.Name.ToUpper().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.Name)
            .Skip(skip)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var planIds = items.Select(item => item.Id).ToList();
        var catalogCounts = await _dbContext.PlanCatalogs
            .AsNoTracking()
            .Where(item => planIds.Contains(item.PlanId))
            .GroupBy(item => item.PlanId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken);

        var results = items.Select(item =>
        {
            catalogCounts.TryGetValue(item.Id, out var count);
            return new AdminPlanResult
            {
                Id = item.Id,
                Name = item.Name,
                BillingPeriod = item.BillingPeriod,
                IsActive = item.IsActive,
                CatalogCount = count,
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt
            };
        }).ToList();

        return ServiceResult<PagedResult<AdminPlanResult>>.Success(new PagedResult<AdminPlanResult>
        {
            Items = results,
            Total = total,
            Skip = skip,
            Limit = limit
        });
    }

    public async Task<ServiceResult<AdminPlanDetailResult>> GetByIdAsync(
        Guid planId,
        CancellationToken cancellationToken = default)
    {
        return await GetByIdInternalAsync(planId, cancellationToken);
    }

    public async Task<ServiceResult<AdminPlanDetailResult>> CreateAsync(
        AdminPlanUpsertRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidatePlanRequest(request, actorUserId);
        if (errors.Count > 0)
            return ServiceResult<AdminPlanDetailResult>.Failure(errors);

        var normalizedName = request.Name.Trim();
        var duplicateName = await _dbContext.Plans.AnyAsync(
            item => item.Name == normalizedName,
            cancellationToken);

        if (duplicateName)
            return ServiceResult<AdminPlanDetailResult>.Failure(new[]
            {
                new ValidationError("name", "Plan name already exists")
            });

        var plan = new Plan
        {
            Name = normalizedName,
            BillingPeriod = ResolveBillingPeriod(request.BillingPeriod),
            IsActive = request.IsActive,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.Plans.Add(plan);
        AddAuditEvent(actorUserId, "AdminPlans.Create", nameof(Plan), plan.Id, new { plan.Name, plan.IsActive });
        await _dbContext.SaveChangesAsync(cancellationToken);

        return await GetByIdInternalAsync(plan.Id, cancellationToken);
    }

    public async Task<ServiceResult<AdminPlanDetailResult>> UpdateAsync(
        Guid planId,
        AdminPlanUpsertRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidatePlanRequest(request, actorUserId);
        if (errors.Count > 0)
            return ServiceResult<AdminPlanDetailResult>.Failure(errors);

        var plan = await _dbContext.Plans.FirstOrDefaultAsync(item => item.Id == planId, cancellationToken);

        if (plan == null)
            return ServiceResult<AdminPlanDetailResult>.Failure(new[]
            {
                new ValidationError("planId", "Plan not found")
            });

        var normalizedName = request.Name.Trim();
        var duplicateName = await _dbContext.Plans.AnyAsync(
            item => item.Id != planId && item.Name == normalizedName,
            cancellationToken);

        if (duplicateName)
            return ServiceResult<AdminPlanDetailResult>.Failure(new[]
            {
                new ValidationError("name", "Plan name already exists")
            });

        plan.Name = normalizedName;
        plan.BillingPeriod = ResolveBillingPeriod(request.BillingPeriod);
        plan.IsActive = request.IsActive;
        plan.UpdatedAt = DateTimeOffset.UtcNow;

        AddAuditEvent(actorUserId, "AdminPlans.Update", nameof(Plan), plan.Id, new { plan.Name, plan.IsActive });
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await GetByIdInternalAsync(plan.Id, cancellationToken);
    }

    public async Task<ServiceResult<bool>> DeactivateAsync(
        Guid planId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (actorUserId == Guid.Empty)
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("actor", "Actor user is required")
            });

        var plan = await _dbContext.Plans.FirstOrDefaultAsync(item => item.Id == planId, cancellationToken);

        if (plan == null || !plan.IsActive)
            return ServiceResult<bool>.Success(false);

        plan.IsActive = false;
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        AddAuditEvent(actorUserId, "AdminPlans.Deactivate", nameof(Plan), plan.Id, new { plan.Id });
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<AdminPlanDetailResult>> ReplaceCatalogsAsync(
        Guid planId,
        PlanReplaceCatalogsRequest request,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (actorUserId == Guid.Empty)
            return ServiceResult<AdminPlanDetailResult>.Failure(new[]
            {
                new ValidationError("actor", "Actor user is required")
            });

        var plan = await _dbContext.Plans
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == planId, cancellationToken);

        if (plan == null)
            return ServiceResult<AdminPlanDetailResult>.Failure(new[]
            {
                new ValidationError("planId", "Plan not found")
            });

        request ??= new PlanReplaceCatalogsRequest();
        var desiredCatalogIds = (request.CatalogIds ?? new List<Guid>())
            .Where(item => item != Guid.Empty)
            .Distinct()
            .ToList();

        var validCatalogIds = await _dbContext.Catalogs
            .AsNoTracking()
            .Where(item => desiredCatalogIds.Contains(item.Id) && item.IsActive)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);

        var invalidCatalogIds = desiredCatalogIds.Except(validCatalogIds).ToList();

        if (invalidCatalogIds.Count > 0)
        {
            var invalidCatalogErrors = invalidCatalogIds
                .Select(item => new ValidationError("invalidCatalogIds", item.ToString()))
                .ToList();
            invalidCatalogErrors.Add(new ValidationError("catalogIds", "One or more catalog ids are invalid or inactive"));
            return ServiceResult<AdminPlanDetailResult>.Failure(invalidCatalogErrors);
        }

        var efDbContext = (DbContext)_dbContext;
        await using var transaction = await BeginTransactionIfSupportedAsync(efDbContext, cancellationToken);

        var currentRelations = await _dbContext.PlanCatalogs
            .Where(item => item.PlanId == planId)
            .ToListAsync(cancellationToken);

        var currentSet = currentRelations.Select(item => item.CatalogId).ToHashSet();
        var desiredSet = desiredCatalogIds.ToHashSet();

        var toRemove = currentRelations.Where(item => !desiredSet.Contains(item.CatalogId)).ToList();
        var toAdd = desiredCatalogIds.Where(item => !currentSet.Contains(item)).ToList();

        if (toRemove.Count > 0)
            _dbContext.PlanCatalogs.RemoveRange(toRemove);

        if (toAdd.Count > 0)
        {
            _dbContext.PlanCatalogs.AddRange(toAdd.Select(item => new PlanCatalog
            {
                PlanId = planId,
                CatalogId = item,
                CreatedAt = DateTimeOffset.UtcNow
            }));
        }

        AddAuditEvent(actorUserId, "AdminPlans.ReplaceCatalogs", nameof(Plan), planId,
            new { Added = toAdd.Count, Removed = toRemove.Count });

        await _dbContext.SaveChangesAsync(cancellationToken);
        if (transaction != null)
            await transaction.CommitAsync(cancellationToken);

        return await GetByIdInternalAsync(planId, cancellationToken);
    }

    private async Task<ServiceResult<AdminPlanDetailResult>> GetByIdInternalAsync(
        Guid planId,
        CancellationToken cancellationToken)
    {
        var plan = await _dbContext.Plans
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == planId, cancellationToken);

        if (plan == null)
            return ServiceResult<AdminPlanDetailResult>.Failure(new[]
            {
                new ValidationError("planId", "Plan not found")
            });

        var catalogIds = await _dbContext.PlanCatalogs
            .AsNoTracking()
            .Where(item => item.PlanId == plan.Id)
            .OrderBy(item => item.CatalogId)
            .Select(item => item.CatalogId)
            .ToListAsync(cancellationToken);

        return ServiceResult<AdminPlanDetailResult>.Success(new AdminPlanDetailResult
        {
            Id = plan.Id,
            Name = plan.Name,
            BillingPeriod = plan.BillingPeriod,
            IsActive = plan.IsActive,
            CatalogIds = catalogIds,
            CreatedAt = plan.CreatedAt,
            UpdatedAt = plan.UpdatedAt
        });
    }

    private static List<ValidationError> ValidatePlanRequest(AdminPlanUpsertRequest request, Guid actorUserId)
    {
        var errors = new List<ValidationError>();
        if (request == null)
        {
            errors.Add(new ValidationError("request", "Request is required"));
            return errors;
        }

        if (string.IsNullOrWhiteSpace(request.Name))
            errors.Add(new ValidationError("name", "Name is required"));
        else if (request.Name.Trim().Length > 200)
            errors.Add(new ValidationError("name", "Name cannot exceed 200 characters"));

        if (actorUserId == Guid.Empty)
            errors.Add(new ValidationError("actor", "Actor user is required"));

        if (request.BillingPeriod.HasValue && !Enum.IsDefined(typeof(BillingPeriod), request.BillingPeriod.Value))
            errors.Add(new ValidationError("billingPeriod", "Billing period is invalid"));

        return errors;
    }

    private static BillingPeriod ResolveBillingPeriod(BillingPeriod? billingPeriod)
    {
        if (billingPeriod.HasValue && Enum.IsDefined(typeof(BillingPeriod), billingPeriod.Value))
            return billingPeriod.Value;
        return BillingPeriod.Monthly;
    }

    private void AddAuditEvent(Guid actorUserId, string action, string entity, Guid entityId, object metadata)
    {
        _dbContext.AuditEvents.Add(new AuditEvent
        {
            TenantId = string.Empty,
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
            return null;
        return await dbContext.Database.BeginTransactionAsync(cancellationToken);
    }
}
