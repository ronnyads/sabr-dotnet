using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Security;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Domain.Protheus;

namespace Phub.Application.Services;

public sealed class PlatformUserService
{
    private const int MinPasswordLength = 8;
    private const string PlatformTenantId = "platform";

    private readonly IAppDbContext _dbContext;

    public PlatformUserService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ServiceResult<PlatformUserListResponse>> ListAsync(
        int skip,
        int limit,
        PlatformUserRole? role,
        bool includeInactive,
        string? search,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.PlatformUsers.AsQueryable();

        if (!includeInactive)
        {
            query = query.Where(u => u.IsActive);
        }

        if (role.HasValue)
        {
            query = query.Where(u => u.Role == role.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(u => u.Name.ToLower().Contains(term) || u.EmailNormalized.Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(u => u.Name)
            .Skip(skip)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return ServiceResult<PlatformUserListResponse>.Success(new PlatformUserListResponse
        {
            Items = items.Select(MapResult).ToList(),
            Total = total,
            Skip = skip,
            Limit = limit
        });
    }

    public async Task<ServiceResult<PlatformUserResult>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.PlatformUsers.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (entity == null)
        {
            return ServiceResult<PlatformUserResult>.Failure(new[]
            {
                new ValidationError("id", "User not found")
            });
        }

        return ServiceResult<PlatformUserResult>.Success(MapResult(entity));
    }

    public async Task<ServiceResult<PlatformUserResult>> CreateAsync(
        PlatformUserCreateRequest request,
        PlatformUserRole actorRole,
        Guid? actorId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateCreateRequest(request);
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        if (actorRole == PlatformUserRole.Admin && request.Role == PlatformUserRole.SuperAdmin)
        {
            errors.Add(new ValidationError("role", "Admin cannot create SuperAdmin"));
        }

        if (await _dbContext.PlatformUsers.AnyAsync(u => u.EmailNormalized == normalizedEmail, cancellationToken))
        {
            errors.Add(new ValidationError("email", "Email already registered"));
        }

        if (await _dbContext.Users.AnyAsync(u => u.Email == normalizedEmail, cancellationToken) ||
            await _dbContext.Clients.AnyAsync(c => c.Email == normalizedEmail, cancellationToken))
        {
            errors.Add(new ValidationError("email", "Email already registered"));
        }

        if (errors.Count > 0)
        {
            return ServiceResult<PlatformUserResult>.Failure(errors);
        }

        var entity = new PlatformUser
        {
            Name = request.Name.Trim(),
            Email = request.Email.Trim(),
            EmailNormalized = normalizedEmail,
            PasswordHash = PasswordHasher.HashPassword(request.Password),
            Role = request.Role,
            IsActive = request.IsActive,
            ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.InternalUserOperator, ProtheusOperationType.CREATE)
        };

        _dbContext.PlatformUsers.Add(entity);
        await AppendOutboxEventAsync(entity, "PlatformUserCreated", requestId, cancellationToken);
        AppendAudit(
            tenantId: PlatformTenantId,
            actorType: "PlatformUser",
            actorId: actorId,
            action: "PlatformUser.Created",
            entity: nameof(PlatformUser),
            entityId: entity.Id,
            requestId: requestId,
            metadata: new { entity.Email, entity.Role });

        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<PlatformUserResult>.Success(MapResult(entity));
    }

    public async Task<ServiceResult<PlatformUserResult>> UpdateAsync(
        Guid id,
        PlatformUserUpdateRequest request,
        PlatformUserRole actorRole,
        Guid? actorId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateUpdateRequest(request);
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        var entity = await _dbContext.PlatformUsers.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (entity == null)
        {
            return ServiceResult<PlatformUserResult>.Failure(new[]
            {
                new ValidationError("id", "User not found")
            });
        }

        if (actorRole == PlatformUserRole.Admin && (request.Role == PlatformUserRole.SuperAdmin || entity.Role == PlatformUserRole.SuperAdmin))
        {
            errors.Add(new ValidationError("role", "Admin cannot manage SuperAdmin"));
        }

        if (entity.Role == PlatformUserRole.SuperAdmin && request.Role != PlatformUserRole.SuperAdmin)
        {
            var activeSuperAdmins = await CountActiveSuperAdminsAsync(cancellationToken);
            if (activeSuperAdmins <= 1)
            {
                errors.Add(new ValidationError("role", "Cannot remove the last active SuperAdmin"));
            }
        }

        if (await _dbContext.PlatformUsers.AnyAsync(u => u.EmailNormalized == normalizedEmail && u.Id != id, cancellationToken))
        {
            errors.Add(new ValidationError("email", "Email already registered"));
        }

        if (await _dbContext.Users.AnyAsync(u => u.Email == normalizedEmail, cancellationToken) ||
            await _dbContext.Clients.AnyAsync(c => c.Email == normalizedEmail, cancellationToken))
        {
            errors.Add(new ValidationError("email", "Email already registered"));
        }

        if (errors.Count > 0)
        {
            return ServiceResult<PlatformUserResult>.Failure(errors);
        }

        entity.Name = request.Name.Trim();
        entity.Email = request.Email.Trim();
        entity.EmailNormalized = normalizedEmail;
        entity.Role = request.Role;
        entity.IsActive = request.IsActive;
        entity.ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.InternalUserOperator, ProtheusOperationType.UPDATE);

        await AppendOutboxEventAsync(entity, "PlatformUserUpdated", requestId, cancellationToken);
        AppendAudit(
            tenantId: PlatformTenantId,
            actorType: "PlatformUser",
            actorId: actorId,
            action: "PlatformUser.Updated",
            entity: nameof(PlatformUser),
            entityId: entity.Id,
            requestId: requestId,
            metadata: new { entity.Email, entity.Role, entity.IsActive });

        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<PlatformUserResult>.Success(MapResult(entity));
    }

    public async Task<ServiceResult<bool>> SetStatusAsync(
        Guid id,
        bool isActive,
        PlatformUserRole actorRole,
        Guid? actorId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.PlatformUsers.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (entity == null)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("id", "User not found")
            });
        }

        if (actorRole == PlatformUserRole.Admin && entity.Role == PlatformUserRole.SuperAdmin)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("role", "Admin cannot manage SuperAdmin")
            });
        }

        if (!isActive && entity.Role == PlatformUserRole.SuperAdmin)
        {
            var activeSuperAdmins = await CountActiveSuperAdminsAsync(cancellationToken);
            if (activeSuperAdmins <= 1)
            {
                return ServiceResult<bool>.Failure(new[]
                {
                    new ValidationError("role", "Cannot deactivate the last active SuperAdmin")
                });
            }
        }

        entity.IsActive = isActive;
        entity.ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.InternalUserOperator, ProtheusOperationType.UPDATE);

        await AppendOutboxEventAsync(entity, "PlatformUserUpdated", requestId, cancellationToken);
        AppendAudit(
            tenantId: PlatformTenantId,
            actorType: "PlatformUser",
            actorId: actorId,
            action: "PlatformUser.StatusChanged",
            entity: nameof(PlatformUser),
            entityId: entity.Id,
            requestId: requestId,
            metadata: new { entity.Email, entity.Role, entity.IsActive });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<bool>> ResetPasswordAsync(
        Guid id,
        string temporaryPassword,
        PlatformUserRole actorRole,
        Guid? actorId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(temporaryPassword) || temporaryPassword.Length < MinPasswordLength)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("temporaryPassword", $"Password must be at least {MinPasswordLength} characters")
            });
        }

        var entity = await _dbContext.PlatformUsers.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (entity == null)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("id", "User not found")
            });
        }

        if (actorRole == PlatformUserRole.Admin && entity.Role == PlatformUserRole.SuperAdmin)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("role", "Admin cannot manage SuperAdmin")
            });
        }

        entity.PasswordHash = PasswordHasher.HashPassword(temporaryPassword);
        entity.ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.InternalUserOperator, ProtheusOperationType.UPDATE);

        await AppendOutboxEventAsync(entity, "PlatformUserUpdated", requestId, cancellationToken);
        AppendAudit(
            tenantId: PlatformTenantId,
            actorType: "PlatformUser",
            actorId: actorId,
            action: "PlatformUser.PasswordReset",
            entity: nameof(PlatformUser),
            entityId: entity.Id,
            requestId: requestId,
            metadata: new { entity.Email, entity.Role });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    private async Task<int> CountActiveSuperAdminsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.PlatformUsers.CountAsync(
            u => u.Role == PlatformUserRole.SuperAdmin && u.IsActive,
            cancellationToken);
    }

    private static List<ValidationError> ValidateCreateRequest(PlatformUserCreateRequest request)
    {
        var errors = ValidateUpdateRequest(new PlatformUserUpdateRequest
        {
            Name = request.Name,
            Email = request.Email,
            Role = request.Role,
            IsActive = request.IsActive
        });

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < MinPasswordLength)
        {
            errors.Add(new ValidationError("password", $"Password must be at least {MinPasswordLength} characters"));
        }

        return errors;
    }

    private static List<ValidationError> ValidateUpdateRequest(PlatformUserUpdateRequest request)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors.Add(new ValidationError("name", "Name is required"));
        }

        if (!BrazilValidators.IsValidEmail(request.Email))
        {
            errors.Add(new ValidationError("email", "Invalid email"));
        }

        if (!Enum.IsDefined(request.Role))
        {
            errors.Add(new ValidationError("role", "Invalid role"));
        }

        return errors;
    }

    private async Task AppendOutboxEventAsync(
        PlatformUser entity,
        string eventType,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (await _dbContext.ProtheusOutboxEvents.AnyAsync(
                o => o.TenantId == PlatformTenantId &&
                     o.CorrelationId == correlationId &&
                     o.EventType == eventType,
                cancellationToken))
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            entity.Id,
            entity.Name,
            entity.Email,
            Role = entity.Role.ToString(),
            entity.IsActive
        });

        _dbContext.ProtheusOutboxEvents.Add(new ProtheusOutboxEvent
        {
            TenantId = PlatformTenantId,
            AggregateType = nameof(PlatformUser),
            AggregateId = entity.Id,
            EventType = eventType,
            PayloadJson = payload,
            Status = OutboxStatus.Pending,
            Attempts = 0,
            CorrelationId = correlationId
        });
    }

    private void AppendAudit(
        string? tenantId,
        string actorType,
        Guid? actorId,
        string action,
        string entity,
        Guid? entityId,
        Guid requestId,
        object metadata)
    {
        _dbContext.AuditEvents.Add(new AuditEvent
        {
            TenantId = tenantId,
            ActorType = actorType,
            ActorId = actorId,
            Action = action,
            Entity = entity,
            EntityId = entityId,
            RequestId = requestId,
            MetadataJson = JsonSerializer.Serialize(metadata)
        });
    }

    private static PlatformUserResult MapResult(PlatformUser entity)
    {
        return new PlatformUserResult
        {
            Id = entity.Id,
            Name = entity.Name,
            Email = entity.Email,
            Role = entity.Role,
            IsActive = entity.IsActive,
            LastLoginAt = entity.LastLoginAt
        };
    }
}
