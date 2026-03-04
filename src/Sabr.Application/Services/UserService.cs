using Microsoft.EntityFrameworkCore;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Security;
using Sabr.Application.Validation;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;
using Sabr.Domain.Protheus;
using System.Text.Json;

namespace Sabr.Application.Services;

public sealed class UserService
{
    private const int MinPasswordLength = 8;

    private readonly IAppDbContext _dbContext;

    public UserService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ServiceResult<UserResult>> CreateAsync(
        UserCreateRequest request,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateCreateRequest(request);
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            errors.Add(new ValidationError("tenantId", "TenantId is required"));
        }

        if (await _dbContext.Users.AnyAsync(u => u.Email == normalizedEmail, cancellationToken))
        {
            errors.Add(new ValidationError("email", "Email already registered"));
        }

        if (await _dbContext.Clients.AnyAsync(c => c.Email == normalizedEmail, cancellationToken))
        {
            errors.Add(new ValidationError("email", "Email already registered"));
        }

        if (errors.Count > 0)
        {
            return ServiceResult<UserResult>.Failure(errors);
        }

        var user = new User
        {
            Name = request.Name.Trim(),
            Email = normalizedEmail,
            PasswordHash = PasswordHasher.HashPassword(request.Password),
            Role = request.Role,
            SectorCode = null,
            TenantId = tenantId,
            IsActive = request.IsActive,
            ProtheusTag = string.Empty,
            ProtheusOperation = ProtheusOperationType.CREATE
        };

        _dbContext.Users.Add(user);
        QueueOutboxEvent(tenantId, user.Id, "TenantUserCreated", new
        {
            user.Id,
            user.TenantId,
            user.Email,
            user.Role,
            user.IsActive
        });
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<UserResult>.Success(MapToResult(user));
    }

    public async Task<ServiceResult<UserResult>> GetAsync(Guid userId, string tenantId, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.Users
            .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId, cancellationToken);

        if (user == null)
        {
            return ServiceResult<UserResult>.Failure(new[]
            {
                new ValidationError("userId", "User not found")
            });
        }

        return ServiceResult<UserResult>.Success(MapToResult(user));
    }

    public async Task<ServiceResult<UserListResponse>> ListAsync(
        string tenantId,
        int skip,
        int limit,
        UserRole? role,
        bool includeInactive,
        string? search,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Users.Where(u => u.TenantId == tenantId);

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
            query = query.Where(u =>
                u.Name.ToLower().Contains(term) ||
                u.Email.ToLower().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip(skip)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return ServiceResult<UserListResponse>.Success(new UserListResponse
        {
            Items = items.Select(MapToResult).ToList(),
            Total = total,
            Skip = skip,
            Limit = limit
        });
    }

    public async Task<ServiceResult<UserResult>> UpdateAsync(
        Guid userId,
        UserUpdateRequest request,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateUpdateRequest(request);
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        var user = await _dbContext.Users
            .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId, cancellationToken);

        if (user == null)
        {
            return ServiceResult<UserResult>.Failure(new[]
            {
                new ValidationError("userId", "User not found")
            });
        }

        if (await _dbContext.Users.AnyAsync(u => u.Email == normalizedEmail && u.Id != userId, cancellationToken))
        {
            errors.Add(new ValidationError("email", "Email already registered"));
        }

        if (await _dbContext.Clients.AnyAsync(c => c.Email == normalizedEmail, cancellationToken))
        {
            errors.Add(new ValidationError("email", "Email already registered"));
        }

        if (errors.Count > 0)
        {
            return ServiceResult<UserResult>.Failure(errors);
        }

        user.Name = request.Name.Trim();
        user.Email = normalizedEmail;
        user.Role = request.Role;
        user.SectorCode = null;
        user.IsActive = request.IsActive;

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            user.PasswordHash = PasswordHasher.HashPassword(request.Password);
        }

        user.ProtheusTag = string.Empty;
        user.ProtheusOperation = ProtheusOperationType.UPDATE;
        QueueOutboxEvent(tenantId, user.Id, "TenantUserUpdated", new
        {
            user.Id,
            user.TenantId,
            user.Email,
            user.Role,
            user.IsActive
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<UserResult>.Success(MapToResult(user));
    }

    public async Task<ServiceResult<bool>> DeactivateAsync(Guid userId, string tenantId, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.Users
            .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId, cancellationToken);

        if (user == null)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("userId", "User not found")
            });
        }

        user.IsActive = false;
        user.SectorCode = null;
        user.ProtheusTag = string.Empty;
        user.ProtheusOperation = ProtheusOperationType.CANCEL;
        QueueOutboxEvent(tenantId, user.Id, "TenantUserUpdated", new
        {
            user.Id,
            user.TenantId,
            user.Email,
            user.Role,
            user.IsActive
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    private void QueueOutboxEvent(string tenantId, Guid userId, string eventType, object payload)
    {
        _dbContext.ProtheusOutboxEvents.Add(new ProtheusOutboxEvent
        {
            TenantId = tenantId,
            AggregateType = nameof(User),
            AggregateId = userId,
            EventType = eventType,
            PayloadJson = JsonSerializer.Serialize(payload),
            Status = OutboxStatus.Pending,
            Attempts = 0,
            CorrelationId = Guid.NewGuid()
        });
    }

    private static List<ValidationError> ValidateCreateRequest(UserCreateRequest request)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(request.Name))
            errors.Add(new ValidationError("name", "Name is required"));

        if (!BrazilValidators.IsValidEmail(request.Email))
            errors.Add(new ValidationError("email", "Invalid email"));

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < MinPasswordLength)
            errors.Add(new ValidationError("password", $"Password must be at least {MinPasswordLength} characters"));

        return errors;
    }

    private static List<ValidationError> ValidateUpdateRequest(UserUpdateRequest request)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(request.Name))
            errors.Add(new ValidationError("name", "Name is required"));

        if (!BrazilValidators.IsValidEmail(request.Email))
            errors.Add(new ValidationError("email", "Invalid email"));

        if (!string.IsNullOrWhiteSpace(request.Password) && request.Password.Length < MinPasswordLength)
            errors.Add(new ValidationError("password", $"Password must be at least {MinPasswordLength} characters"));

        return errors;
    }

    private static UserResult MapToResult(User user)
    {
        return new UserResult
        {
            Id = user.Id,
            TenantId = user.TenantId,
            Name = user.Name,
            Email = user.Email,
            Role = user.Role,
            SectorCode = null,
            IsActive = user.IsActive,
            LastLoginAt = user.LastLoginAt
        };
    }
}
