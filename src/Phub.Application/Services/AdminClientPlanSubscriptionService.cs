using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class AdminClientPlanSubscriptionService
{
    private readonly IAppDbContext _dbContext;

    public AdminClientPlanSubscriptionService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ServiceResult<ClientPlanSubscriptionsResult>> GetCurrentAsync(
        string tenantSlug,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var tenantResult = await ResolveTenantAsync(tenantSlug, cancellationToken);
        if (!tenantResult.Succeeded || tenantResult.Data == null)
        {
            return ServiceResult<ClientPlanSubscriptionsResult>.Failure(tenantResult.Errors);
        }

        var clientValidation = await ValidateClientAsync(tenantResult.Data.Id, clientId, cancellationToken);
        if (!clientValidation.Succeeded)
        {
            return ServiceResult<ClientPlanSubscriptionsResult>.Failure(clientValidation.Errors);
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var items = await GetCurrentItemsAsync(tenantResult.Data.Id, clientId, nowUtc, cancellationToken);

        return ServiceResult<ClientPlanSubscriptionsResult>.Success(new ClientPlanSubscriptionsResult
        {
            ClientId = clientId,
            TenantSlug = tenantResult.Data.Slug,
            Items = items
        });
    }

    public async Task<ServiceResult<ClientPlanSubscriptionsResult>> ReplaceSetAsync(
        string tenantSlug,
        Guid clientId,
        IReadOnlyCollection<Guid>? planIds,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (actorUserId == Guid.Empty)
        {
            return ServiceResult<ClientPlanSubscriptionsResult>.Failure(new[]
            {
                new ValidationError("actor", "Actor user is required")
            });
        }

        var tenantResult = await ResolveTenantAsync(tenantSlug, cancellationToken);
        if (!tenantResult.Succeeded || tenantResult.Data == null)
        {
            return ServiceResult<ClientPlanSubscriptionsResult>.Failure(tenantResult.Errors);
        }

        var tenant = tenantResult.Data;
        var clientValidation = await ValidateClientAsync(tenant.Id, clientId, cancellationToken);
        if (!clientValidation.Succeeded)
        {
            return ServiceResult<ClientPlanSubscriptionsResult>.Failure(clientValidation.Errors);
        }

        var desiredPlanIds = (planIds ?? Array.Empty<Guid>())
            .Where(item => item != Guid.Empty)
            .Distinct()
            .OrderBy(item => item)
            .ToList();

        var desiredPlans = await _dbContext.Plans
            .Where(item => desiredPlanIds.Contains(item.Id))
            .ToListAsync(cancellationToken);

        var existingPlanIds = desiredPlans.Select(item => item.Id).ToHashSet();
        var invalidPlanIds = desiredPlanIds
            .Where(item => !existingPlanIds.Contains(item))
            .OrderBy(item => item)
            .ToList();

        var inactivePlanIds = desiredPlans
            .Where(item => !item.IsActive)
            .Select(item => item.Id)
            .OrderBy(item => item)
            .ToList();

        if (invalidPlanIds.Count > 0 || inactivePlanIds.Count > 0)
        {
            var errors = new List<ValidationError>();
            errors.AddRange(invalidPlanIds.Select(item => new ValidationError("invalidPlanIds", item.ToString())));
            errors.AddRange(inactivePlanIds.Select(item => new ValidationError("inactivePlanIds", item.ToString())));

            if (invalidPlanIds.Count > 0)
            {
                errors.Add(new ValidationError("planIds", "One or more plan ids are invalid"));
            }

            if (inactivePlanIds.Count > 0)
            {
                errors.Add(new ValidationError("planIds", "One or more plan ids are inactive"));
            }

            return ServiceResult<ClientPlanSubscriptionsResult>.Failure(errors);
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var efDbContext = (DbContext)_dbContext;
        await using var transaction = await BeginTransactionIfSupportedAsync(efDbContext, cancellationToken);

        var existingActiveSubscriptions = await _dbContext.ClientPlanSubscriptions
            .Where(item => item.TenantId == tenant.Id && item.ClientId == clientId && item.IsActive)
            .ToListAsync(cancellationToken);

        var beforePlanIds = existingActiveSubscriptions
            .Select(item => item.PlanId)
            .Distinct()
            .OrderBy(item => item)
            .ToList();

        var desiredSet = desiredPlanIds.ToHashSet();
        var desiredPlanMap = desiredPlans
            .Where(item => item.IsActive)
            .ToDictionary(item => item.Id, item => item);

        foreach (var subscription in existingActiveSubscriptions.Where(item => !desiredSet.Contains(item.PlanId)))
        {
            subscription.IsActive = false;
            subscription.EndsAt = nowUtc;
            subscription.UpdatedAt = nowUtc;
        }

        foreach (var desiredPlanId in desiredPlanIds)
        {
            var activeForPlan = existingActiveSubscriptions
                .Where(item => item.PlanId == desiredPlanId && item.IsActive)
                .OrderByDescending(item => item.StartsAt)
                .ThenByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .ToList();

            var hasValidWindow = activeForPlan.Any(item =>
                item.StartsAt <= nowUtc &&
                item.EndsAt.HasValue &&
                nowUtc < item.EndsAt.Value);

            if (hasValidWindow)
            {
                continue;
            }

            foreach (var stale in activeForPlan)
            {
                stale.IsActive = false;
                stale.EndsAt = nowUtc;
                stale.UpdatedAt = nowUtc;
            }

            if (!desiredPlanMap.TryGetValue(desiredPlanId, out var desiredPlan))
            {
                continue;
            }

            var months = MapBillingPeriodToMonths(desiredPlan.BillingPeriod);
            _dbContext.ClientPlanSubscriptions.Add(new ClientPlanSubscription
            {
                TenantId = tenant.Id,
                ClientId = clientId,
                PlanId = desiredPlanId,
                IsActive = true,
                StartsAt = nowUtc,
                EndsAt = nowUtc.AddMonths(months),
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        var currentItems = await GetCurrentItemsAsync(tenant.Id, clientId, nowUtc, cancellationToken);
        var afterPlanIds = currentItems
            .Select(item => item.PlanId)
            .Distinct()
            .OrderBy(item => item)
            .ToList();

        AddAuditEvent(tenant.Id, actorUserId, clientId, beforePlanIds, afterPlanIds);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<ClientPlanSubscriptionsResult>.Success(new ClientPlanSubscriptionsResult
        {
            ClientId = clientId,
            TenantSlug = tenant.Slug,
            Items = currentItems
        });
    }

    private async Task<List<ClientPlanSubscriptionItemResult>> GetCurrentItemsAsync(
        string tenantId,
        Guid clientId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        return await (
                from subscription in _dbContext.ClientPlanSubscriptions
                join plan in _dbContext.Plans on subscription.PlanId equals plan.Id
                where subscription.TenantId == tenantId
                      && subscription.ClientId == clientId
                      && subscription.IsActive
                      && subscription.StartsAt <= nowUtc
                      && subscription.EndsAt.HasValue
                      && nowUtc < subscription.EndsAt.Value
                select new ClientPlanSubscriptionItemResult
                {
                    PlanId = subscription.PlanId,
                    BillingPeriod = plan.BillingPeriod,
                    StartsAt = subscription.StartsAt,
                    EndsAt = subscription.EndsAt!.Value
                })
            .Distinct()
            .OrderBy(item => item.EndsAt)
            .ThenBy(item => item.PlanId)
            .ToListAsync(cancellationToken);
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

    private async Task<ServiceResult<bool>> ValidateClientAsync(string tenantId, Guid clientId, CancellationToken cancellationToken)
    {
        if (clientId == Guid.Empty)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("clientId", "Client id is required")
            });
        }

        var exists = await _dbContext.Clients.AnyAsync(
            item => item.TenantId == tenantId && item.Id == clientId,
            cancellationToken);

        if (!exists)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("clientId", "Client not found")
            });
        }

        return ServiceResult<bool>.Success(true);
    }

    private void AddAuditEvent(
        string tenantId,
        Guid actorUserId,
        Guid clientId,
        IReadOnlyCollection<Guid> beforePlanIds,
        IReadOnlyCollection<Guid> afterPlanIds)
    {
        _dbContext.AuditEvents.Add(new AuditEvent
        {
            TenantId = tenantId,
            ActorType = "AdminUser",
            ActorId = actorUserId,
            Action = "AdminClients.ReplacePlanSubscriptions",
            Entity = nameof(ClientPlanSubscription),
            EntityId = clientId,
            RequestId = Guid.NewGuid(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                ClientId = clientId,
                BeforePlanIds = beforePlanIds.OrderBy(item => item).ToArray(),
                AfterPlanIds = afterPlanIds.OrderBy(item => item).ToArray()
            })
        });
    }

    private static int MapBillingPeriodToMonths(BillingPeriod billingPeriod)
    {
        return billingPeriod switch
        {
            BillingPeriod.Monthly => 1,
            BillingPeriod.Quarterly => 3,
            BillingPeriod.Semiannual => 6,
            BillingPeriod.Annual => 12,
            _ => 1
        };
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
