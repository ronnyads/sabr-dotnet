using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Security;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using System.Security.Cryptography;
using System.Text;

namespace Phub.Application.Services;

public sealed class SupplierAuthService
{
    private const int MinPasswordLength = 8;

    private readonly IAppDbContext _dbContext;

    public SupplierAuthService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ServiceResult<AuthUserResult>> LoginAsync(
        AuthLoginRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<ValidationError>();
        if (!BrazilValidators.IsValidEmail(request.Email))
            errors.Add(new ValidationError("email", "Invalid email"));
        if (string.IsNullOrWhiteSpace(request.Password))
            errors.Add(new ValidationError("password", "Password is required"));
        if (errors.Count > 0)
            return ServiceResult<AuthUserResult>.Failure(errors);

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var supplier = await _dbContext.Suppliers
            .FirstOrDefaultAsync(s => s.EmailNormalized == normalizedEmail && s.IsActive, cancellationToken);

        if (supplier == null || !PasswordHasher.VerifyPassword(request.Password, supplier.PasswordHash))
        {
            return ServiceResult<AuthUserResult>.Failure(new[]
            {
                new ValidationError("credentials", "Invalid credentials")
            });
        }

        supplier.LastLoginAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<AuthUserResult>.Success(MapResult(supplier));
    }

    public async Task<ServiceResult<AuthTokenResult>> RefreshAsync(
        string refreshToken,
        string? ip,
        string? userAgent,
        int refreshTokenDays,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return ServiceResult<AuthTokenResult>.Failure(new[]
            {
                new ValidationError("refresh", "Invalid refresh token")
            });
        }

        var tokenHash = HashToken(refreshToken);
        var token = await _dbContext.SupplierRefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

        if (token == null || token.RevokedAt.HasValue || token.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return ServiceResult<AuthTokenResult>.Failure(new[]
            {
                new ValidationError("refresh", "Invalid refresh token")
            });
        }

        var supplier = await _dbContext.Suppliers
            .FirstOrDefaultAsync(s => s.Id == token.SupplierId && s.IsActive, cancellationToken);

        if (supplier == null)
        {
            return ServiceResult<AuthTokenResult>.Failure(new[]
            {
                new ValidationError("refresh", "Invalid refresh token")
            });
        }

        var newRaw = GenerateToken();
        var newEntity = new SupplierRefreshToken
        {
            SupplierId = supplier.Id,
            TokenHash = HashToken(newRaw),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(refreshTokenDays),
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByIp = ip,
            CreatedByUserAgent = userAgent
        };

        token.RevokedAt = DateTimeOffset.UtcNow;
        token.ReplacedById = newEntity.Id;

        _dbContext.SupplierRefreshTokens.Add(newEntity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<AuthTokenResult>.Success(new AuthTokenResult
        {
            User = MapResult(supplier),
            RefreshToken = newRaw
        });
    }

    public async Task<string> IssueRefreshTokenAsync(
        Guid supplierId,
        int days,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var raw = GenerateToken();
        var entity = new SupplierRefreshToken
        {
            SupplierId = supplierId,
            TokenHash = HashToken(raw),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(days),
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByIp = ip,
            CreatedByUserAgent = userAgent
        };

        _dbContext.SupplierRefreshTokens.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return raw;
    }

    public async Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return;

        var tokenHash = HashToken(refreshToken);
        var token = await _dbContext.SupplierRefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

        if (token == null || token.RevokedAt.HasValue)
            return;

        token.RevokedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ServiceResult<AuthUserResult>> RegisterAsync(
        SupplierRegisterRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<ValidationError>();
        if (string.IsNullOrWhiteSpace(request.Name))
            errors.Add(new ValidationError("name", "Name is required"));
        if (!BrazilValidators.IsValidEmail(request.Email))
            errors.Add(new ValidationError("email", "Invalid email"));
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < MinPasswordLength)
            errors.Add(new ValidationError("password", $"Password must be at least {MinPasswordLength} characters"));
        if (errors.Count > 0)
            return ServiceResult<AuthUserResult>.Failure(errors);

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var exists = await _dbContext.Suppliers
            .AnyAsync(s => s.EmailNormalized == normalizedEmail, cancellationToken);

        if (exists)
        {
            return ServiceResult<AuthUserResult>.Failure(new[]
            {
                new ValidationError("email", "Email already registered")
            });
        }

        var supplier = new Supplier
        {
            Name = request.Name.Trim(),
            Email = request.Email.Trim(),
            EmailNormalized = normalizedEmail,
            PasswordHash = PasswordHasher.HashPassword(request.Password),
            Status = SupplierStatus.PendingApproval,
            IsActive = false,
            CompanyName = request.CompanyName?.Trim(),
            Phone = request.Phone?.Trim(),
            Document = request.Document?.Trim(),
            TenantId = request.TenantId
        };

        _dbContext.Suppliers.Add(supplier);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<AuthUserResult>.Success(MapResult(supplier));
    }

    private static AuthUserResult MapResult(Supplier supplier)
    {
        return new AuthUserResult
        {
            Id = supplier.Id,
            TenantId = supplier.TenantId ?? string.Empty,
            Name = supplier.Name,
            Email = supplier.Email,
            AccountType = "supplier",
            Scope = "supplier",
            IsActive = supplier.IsActive
        };
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        var base64 = Convert.ToBase64String(bytes);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string HashToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }
}
