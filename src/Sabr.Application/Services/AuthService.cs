using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Phub.Application.Security;
using Phub.Application.Validation;
using Phub.Domain.Protheus;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class AuthService
{
    private const int MinPasswordLength = 8;
    private const string PlatformTenantId = "platform";
    public const string TenantAmbiguousCode = "TENANT_AMBIGUOUS";
    private static readonly HashSet<string> AllowedSectorCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ProtheusPrefixes.InternalUserRh,
        ProtheusPrefixes.InternalUserPurchasing,
        ProtheusPrefixes.InternalUserStock,
        ProtheusPrefixes.InternalUserAccounting,
        ProtheusPrefixes.InternalUserExpedition,
        ProtheusPrefixes.InternalUserOperator
    };

    private readonly IAppDbContext _dbContext;

    public AuthService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ServiceResult<AuthUserResult>> LoginAsync(
        string tenantId,
        AuthLoginRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateLoginRequest(request);
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            errors.Add(new ValidationError("tenantId", "TenantId is required"));
        }

        if (errors.Count > 0)
        {
            return ServiceResult<AuthUserResult>.Failure(errors);
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var normalizedTenant = tenantId.Trim();

        var user = await _dbContext.Users
            .FirstOrDefaultAsync(u => u.Email == normalizedEmail && u.TenantId == normalizedTenant, cancellationToken);

        if (user != null)
        {
            if (!user.IsActive || !PasswordHasher.VerifyPassword(request.Password, user.PasswordHash))
            {
                return InvalidCredentialsFailure();
            }

            user.LastLoginAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            var mappedUser = MapAdminResult(user);
            mappedUser.TenantSlug = await ResolveTenantSlugAsync(user.TenantId, cancellationToken);
            return ServiceResult<AuthUserResult>.Success(mappedUser);
        }

        var client = await _dbContext.Clients
            .FirstOrDefaultAsync(c => c.Email == normalizedEmail && c.TenantId == normalizedTenant, cancellationToken);

        if (client == null || client.Status == ClientStatus.Inactive)
        {
            return InvalidCredentialsFailure();
        }

        if (!PasswordHasher.VerifyPassword(request.Password, client.PasswordHash))
        {
            return InvalidCredentialsFailure();
        }

        client.LastLoginAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var mappedClient = MapClientResult(client);
        mappedClient.TenantSlug = await ResolveTenantSlugAsync(client.TenantId, cancellationToken);
        return ServiceResult<AuthUserResult>.Success(mappedClient);
    }

    public async Task<ServiceResult<AuthUserResult>> LoginPlatformAsync(
        AuthLoginRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateLoginRequest(request);
        if (errors.Count > 0)
        {
            return ServiceResult<AuthUserResult>.Failure(errors);
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _dbContext.PlatformUsers
            .FirstOrDefaultAsync(u => u.Email == normalizedEmail && u.IsActive, cancellationToken);

        if (user == null || !PasswordHasher.VerifyPassword(request.Password, user.PasswordHash))
        {
            return InvalidCredentialsFailure();
        }

        var mapped = MapPlatformResult(user);
        mapped.TenantId = PlatformTenantId;
        mapped.TenantSlug = null;
        mapped.Scope = "platform";
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<AuthUserResult>.Success(mapped);
    }

    public async Task<ServiceResult<AuthUserResult>> LoginAutoTenantAsync(
        AuthLoginRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateLoginRequest(request);
        if (errors.Count > 0)
        {
            return ServiceResult<AuthUserResult>.Failure(errors);
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        var adminCandidates = await (
            from user in _dbContext.Users
            join tenant in _dbContext.Tenants on user.TenantId equals tenant.Id
            where user.Email == normalizedEmail &&
                  user.IsActive &&
                  tenant.Status == TenantStatus.Active
            select new AdminLoginCandidate(user, tenant.Slug)
        ).ToListAsync(cancellationToken);

        var clientCandidates = await (
            from client in _dbContext.Clients
            join tenant in _dbContext.Tenants on client.TenantId equals tenant.Id
            where client.Email == normalizedEmail &&
                  client.Status != ClientStatus.Inactive &&
                  tenant.Status == TenantStatus.Active
            select new ClientLoginCandidate(client, tenant.Slug)
        ).ToListAsync(cancellationToken);

        var validAdminCandidates = adminCandidates
            .Where(candidate => PasswordHasher.VerifyPassword(request.Password, candidate.User.PasswordHash))
            .ToList();
        var validClientCandidates = clientCandidates
            .Where(candidate => PasswordHasher.VerifyPassword(request.Password, candidate.Client.PasswordHash))
            .ToList();

        if (validAdminCandidates.Count == 0 && validClientCandidates.Count == 0)
        {
            return InvalidCredentialsFailure();
        }

        var matchedTenantIds = validAdminCandidates
            .Select(candidate => candidate.User.TenantId)
            .Concat(validClientCandidates.Select(candidate => candidate.Client.TenantId))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (matchedTenantIds.Count > 1)
        {
            return ServiceResult<AuthUserResult>.Failure(new[]
            {
                new ValidationError("code", TenantAmbiguousCode),
                new ValidationError("credentials", "Multiple tenant matches")
            });
        }

        if (validAdminCandidates.Count > 0)
        {
            var selected = validAdminCandidates[0];
            selected.User.LastLoginAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            var mappedAdmin = MapAdminResult(selected.User);
            mappedAdmin.TenantSlug = selected.TenantSlug;
            return ServiceResult<AuthUserResult>.Success(mappedAdmin);
        }

        var matchedClient = validClientCandidates[0];
        matchedClient.Client.LastLoginAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var mappedClient = MapClientResult(matchedClient.Client);
        mappedClient.TenantSlug = matchedClient.TenantSlug;
        return ServiceResult<AuthUserResult>.Success(mappedClient);
    }

    public async Task<ServiceResult<AuthTokenResult>> RefreshAsync(
        string tenantId,
        string refreshToken,
        string? ip,
        string? userAgent,
        int refreshTokenDays,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(refreshToken))
        {
            return ServiceResult<AuthTokenResult>.Failure(new[]
            {
                new ValidationError("refresh", "Invalid refresh token")
            });
        }

        var tokenHash = HashToken(refreshToken);
        var token = await _dbContext.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.TenantId == tenantId, cancellationToken);

        if (token != null)
        {
            if (token.RevokedAt.HasValue || token.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return ServiceResult<AuthTokenResult>.Failure(new[]
                {
                    new ValidationError("refresh", "Invalid refresh token")
                });
            }

            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == token.UserId, cancellationToken);
            if (user == null || !user.IsActive || !string.Equals(user.TenantId, tenantId, StringComparison.Ordinal))
            {
                return ServiceResult<AuthTokenResult>.Failure(new[]
                {
                    new ValidationError("refresh", "Invalid refresh token")
                });
            }

            var newRefreshToken = GenerateToken();
            var newRefreshEntity = new RefreshToken
            {
                UserId = user.Id,
                TenantId = tenantId,
                TokenHash = HashToken(newRefreshToken),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(refreshTokenDays),
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByIp = ip,
                CreatedByUserAgent = userAgent
            };

            token.RevokedAt = DateTimeOffset.UtcNow;
            token.ReplacedById = newRefreshEntity.Id;

            _dbContext.RefreshTokens.Add(newRefreshEntity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var mappedUser = MapAdminResult(user);
            mappedUser.TenantSlug = await ResolveTenantSlugAsync(tenantId, cancellationToken);

            return ServiceResult<AuthTokenResult>.Success(new AuthTokenResult
            {
                User = mappedUser,
                RefreshToken = newRefreshToken
            });
        }

        var clientToken = await _dbContext.ClientRefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.TenantId == tenantId, cancellationToken);

        if (clientToken == null || clientToken.RevokedAt.HasValue || clientToken.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return ServiceResult<AuthTokenResult>.Failure(new[]
            {
                new ValidationError("refresh", "Invalid refresh token")
            });
        }

        var client = await _dbContext.Clients.FirstOrDefaultAsync(c => c.Id == clientToken.ClientId, cancellationToken);
        if (client == null || client.Status == ClientStatus.Inactive || !string.Equals(client.TenantId, tenantId, StringComparison.Ordinal))
        {
            return ServiceResult<AuthTokenResult>.Failure(new[]
            {
                new ValidationError("refresh", "Invalid refresh token")
            });
        }

        var newClientRefreshToken = GenerateToken();
        var newClientRefreshEntity = new ClientRefreshToken
        {
            ClientId = client.Id,
            TenantId = tenantId,
            TokenHash = HashToken(newClientRefreshToken),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(refreshTokenDays),
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByIp = ip,
            CreatedByUserAgent = userAgent
        };

        clientToken.RevokedAt = DateTimeOffset.UtcNow;
        clientToken.ReplacedById = newClientRefreshEntity.Id;

        _dbContext.ClientRefreshTokens.Add(newClientRefreshEntity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var mappedClient = MapClientResult(client);
        mappedClient.TenantSlug = await ResolveTenantSlugAsync(tenantId, cancellationToken);

        return ServiceResult<AuthTokenResult>.Success(new AuthTokenResult
        {
            User = mappedClient,
            RefreshToken = newClientRefreshToken
        });
    }

    public async Task<ServiceResult<AuthTokenResult>> RefreshPlatformAsync(
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
        var token = await _dbContext.PlatformRefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

        if (token == null || token.RevokedAt.HasValue || token.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return ServiceResult<AuthTokenResult>.Failure(new[]
            {
                new ValidationError("refresh", "Invalid refresh token")
            });
        }

        var user = await _dbContext.PlatformUsers.FirstOrDefaultAsync(u => u.Id == token.PlatformUserId, cancellationToken);
        if (user == null || !user.IsActive)
        {
            return ServiceResult<AuthTokenResult>.Failure(new[]
            {
                new ValidationError("refresh", "Invalid refresh token")
            });
        }

        var newRefreshToken = GenerateToken();
        var newRefreshEntity = new PlatformRefreshToken
        {
            PlatformUserId = user.Id,
            TokenHash = HashToken(newRefreshToken),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(refreshTokenDays),
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByIp = ip,
            CreatedByUserAgent = userAgent
        };

        token.RevokedAt = DateTimeOffset.UtcNow;
        token.ReplacedById = newRefreshEntity.Id;

        _dbContext.PlatformRefreshTokens.Add(newRefreshEntity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var resultUser = MapPlatformResult(user);
        resultUser.TenantId = PlatformTenantId;
        resultUser.Scope = "platform";

        return ServiceResult<AuthTokenResult>.Success(new AuthTokenResult
        {
            User = resultUser,
            RefreshToken = newRefreshToken
        });
    }

    public async Task<string> IssueRefreshTokenAsync(
        Guid userId,
        string tenantId,
        int days,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var refreshToken = GenerateToken();
        var refreshEntity = new RefreshToken
        {
            UserId = userId,
            TenantId = tenantId,
            TokenHash = HashToken(refreshToken),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(days),
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByIp = ip,
            CreatedByUserAgent = userAgent
        };

        _dbContext.RefreshTokens.Add(refreshEntity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return refreshToken;
    }

    public Task<string> IssuePlatformRefreshTokenAsync(
        Guid userId,
        int days,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        return IssuePlatformRefreshTokenCoreAsync(userId, days, ip, userAgent, cancellationToken);
    }

    public async Task<string> IssueClientRefreshTokenAsync(
        Guid clientId,
        string tenantId,
        int days,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var refreshToken = GenerateToken();
        var refreshEntity = new ClientRefreshToken
        {
            ClientId = clientId,
            TenantId = tenantId,
            TokenHash = HashToken(refreshToken),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(days),
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByIp = ip,
            CreatedByUserAgent = userAgent
        };

        _dbContext.ClientRefreshTokens.Add(refreshEntity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return refreshToken;
    }

    public async Task RevokeRefreshTokenAsync(string tenantId, string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        if (string.Equals(tenantId, PlatformTenantId, StringComparison.OrdinalIgnoreCase))
        {
            var platformTokenHash = HashToken(refreshToken);
            var platformToken = await _dbContext.PlatformRefreshTokens
                .FirstOrDefaultAsync(t => t.TokenHash == platformTokenHash, cancellationToken);

            if (platformToken != null && !platformToken.RevokedAt.HasValue)
            {
                platformToken.RevokedAt = DateTimeOffset.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        var tokenHash = HashToken(refreshToken);
        var token = await _dbContext.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.TenantId == tenantId, cancellationToken);

        if (token != null)
        {
            if (token.RevokedAt.HasValue)
            {
                return;
            }

            token.RevokedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        var clientToken = await _dbContext.ClientRefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.TenantId == tenantId, cancellationToken);

        if (clientToken == null || clientToken.RevokedAt.HasValue)
        {
            return;
        }

        clientToken.RevokedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ServiceResult<AuthUserResult>> BootstrapAdminAsync(
        BootstrapAdminRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateBootstrapRequest(request);
        if (errors.Count > 0)
        {
            return ServiceResult<AuthUserResult>.Failure(errors);
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var normalizedSlug = request.TenantSlug.Trim().ToLowerInvariant();
        var normalizedTenantName = request.TenantName.Trim();
        var sectorCode = request.SectorCode.Trim().ToUpperInvariant();
        var role = request.Role == 0 ? UserRole.SuperAdmin : request.Role;

        if (role != UserRole.Admin && role != UserRole.SuperAdmin)
        {
            return ServiceResult<AuthUserResult>.Failure(new[]
            {
                new ValidationError("role", "Role must be Admin or SuperAdmin")
            });
        }

        var tenant = await _dbContext.Tenants
            .FirstOrDefaultAsync(t => t.Slug == normalizedSlug, cancellationToken);

        if (tenant == null)
        {
            tenant = new Tenant
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = normalizedTenantName,
                Slug = normalizedSlug,
                Status = TenantStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _dbContext.Tenants.Add(tenant);
        }

        var existingUser = await _dbContext.Users
            .FirstOrDefaultAsync(u => u.Email == normalizedEmail && u.TenantId == tenant.Id, cancellationToken);
        var user = existingUser ?? new Domain.Entities.User
        {
            Name = request.Name.Trim(),
            Email = normalizedEmail,
            Role = role,
            SectorCode = sectorCode,
            TenantId = tenant.Id,
            IsActive = true,
            ProtheusTag = ProtheusTag.Build(sectorCode, ProtheusOperationType.CREATE),
            ProtheusOperation = ProtheusOperationType.CREATE
        };

        user.Name = request.Name.Trim();
        user.PasswordHash = PasswordHasher.HashPassword(request.Password);
        user.Role = role;
        user.SectorCode = sectorCode;
        user.IsActive = true;

        var platformUser = await _dbContext.PlatformUsers
            .FirstOrDefaultAsync(p => p.Email == normalizedEmail, cancellationToken);

        if (platformUser == null)
        {
            platformUser = new PlatformUser
            {
                Name = request.Name.Trim(),
                Email = normalizedEmail,
                EmailNormalized = normalizedEmail,
                Role = ToPlatformRole(role),
                ProtheusTag = ProtheusTag.Build(sectorCode, ProtheusOperationType.CREATE),
                IsActive = true
            };
        }

        platformUser.Name = request.Name.Trim();
        platformUser.PasswordHash = user.PasswordHash;
        platformUser.Role = ToPlatformRole(role);
        platformUser.IsActive = true;

        if (existingUser == null)
        {
            _dbContext.Users.Add(user);
        }

        if (platformUser.Id == Guid.Empty)
        {
            _dbContext.PlatformUsers.Add(platformUser);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<AuthUserResult>.Success(MapPlatformResult(platformUser));
    }

    public async Task<ServiceResult<bool>> ChangeClientPasswordAsync(
        Guid clientId,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < MinPasswordLength)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("password", $"Password must be at least {MinPasswordLength} characters")
            });
        }

        var client = await _dbContext.Clients.FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);
        if (client == null || client.Status == ClientStatus.Inactive)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("clientId", "Client not found")
            });
        }

        client.PasswordHash = PasswordHasher.HashPassword(newPassword);
        client.MustChangePassword = false;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<bool>.Success(true);
    }

    private static ServiceResult<AuthUserResult> InvalidCredentialsFailure()
    {
        return ServiceResult<AuthUserResult>.Failure(new[]
        {
            new ValidationError("credentials", "Invalid credentials")
        });
    }

    private async Task<string?> ResolveTenantSlugAsync(string tenantId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        return await _dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Slug)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static List<ValidationError> ValidateLoginRequest(AuthLoginRequest request)
    {
        var errors = new List<ValidationError>();

        if (!BrazilValidators.IsValidEmail(request.Email))
            errors.Add(new ValidationError("email", "Invalid email"));

        if (string.IsNullOrWhiteSpace(request.Password))
            errors.Add(new ValidationError("password", "Password is required"));

        return errors;
    }

    private static List<ValidationError> ValidateBootstrapRequest(BootstrapAdminRequest request)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(request.TenantSlug))
            errors.Add(new ValidationError("tenantSlug", "Tenant slug is required"));

        if (string.IsNullOrWhiteSpace(request.TenantName))
            errors.Add(new ValidationError("tenantName", "Tenant name is required"));

        if (string.IsNullOrWhiteSpace(request.Name))
            errors.Add(new ValidationError("name", "Name is required"));

        if (!BrazilValidators.IsValidEmail(request.Email))
            errors.Add(new ValidationError("email", "Invalid email"));

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < MinPasswordLength)
            errors.Add(new ValidationError("password", $"Password must be at least {MinPasswordLength} characters"));

        if (request.Role != 0 && request.Role != UserRole.Admin && request.Role != UserRole.SuperAdmin)
            errors.Add(new ValidationError("role", "Role must be Admin or SuperAdmin"));

        if (string.IsNullOrWhiteSpace(request.SectorCode))
            errors.Add(new ValidationError("sectorCode", "Sector code is required"));

        if (!string.IsNullOrWhiteSpace(request.SectorCode) && !AllowedSectorCodes.Contains(request.SectorCode.Trim()))
            errors.Add(new ValidationError("sectorCode", "Invalid sector code"));

        if (string.IsNullOrWhiteSpace(request.AdminKey))
            errors.Add(new ValidationError("adminKey", "Admin key is required"));

        if (!string.IsNullOrWhiteSpace(request.TenantSlug))
        {
            var normalized = request.TenantSlug.Trim().ToLowerInvariant();
            if (!Regex.IsMatch(normalized, "^[a-z0-9-]{3,50}$"))
            {
                errors.Add(new ValidationError("tenantSlug", "Tenant slug must be 3-50 chars (a-z, 0-9, -)"));
            }

            if (string.Equals(normalized, PlatformTenantId, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ValidationError("tenantSlug", "Tenant slug is reserved"));
            }
        }

        return errors;
    }

    private static AuthUserResult MapAdminResult(User user)
    {
        return new AuthUserResult
        {
            Id = user.Id,
            TenantId = user.TenantId,
            Name = user.Name,
            Email = user.Email,
            AccountType = AccountTypes.Admin,
            Role = user.Role,
            SectorCode = user.SectorCode,
            IsActive = user.IsActive
        };
    }

    private static AuthUserResult MapPlatformResult(PlatformUser user)
    {
        return new AuthUserResult
        {
            Id = user.Id,
            TenantId = PlatformTenantId,
            Name = user.Name,
            Email = user.Email,
            AccountType = AccountTypes.Admin,
            Scope = "platform",
            Role = ToUserRole(user.Role),
            SectorCode = null,
            IsActive = user.IsActive
        };
    }

    private async Task<string> IssuePlatformRefreshTokenCoreAsync(
        Guid platformUserId,
        int days,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var refreshToken = GenerateToken();
        var refreshEntity = new PlatformRefreshToken
        {
            PlatformUserId = platformUserId,
            TokenHash = HashToken(refreshToken),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(days),
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByIp = ip,
            CreatedByUserAgent = userAgent
        };

        _dbContext.PlatformRefreshTokens.Add(refreshEntity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return refreshToken;
    }

    private static PlatformUserRole ToPlatformRole(UserRole role)
    {
        return role switch
        {
            UserRole.SuperAdmin => PlatformUserRole.SuperAdmin,
            UserRole.Admin => PlatformUserRole.Admin,
            UserRole.Finance => PlatformUserRole.Finance,
            _ => PlatformUserRole.Admin
        };
    }

    private static UserRole ToUserRole(PlatformUserRole role)
    {
        return role switch
        {
            PlatformUserRole.SuperAdmin => UserRole.SuperAdmin,
            PlatformUserRole.Finance => UserRole.Finance,
            _ => UserRole.Admin
        };
    }

    private static AuthUserResult MapClientResult(Client client)
    {
        return new AuthUserResult
        {
            Id = client.Id,
            TenantId = client.TenantId,
            Name = client.AccountName,
            Email = client.Email,
            AccountType = AccountTypes.Client,
            Role = null,
            SectorCode = null,
            IsActive = client.Status != ClientStatus.Inactive,
            MustChangePassword = client.MustChangePassword,
            Status = client.Status,
            OnboardingStep = client.OnboardingStep
        };
    }

    private sealed record AdminLoginCandidate(User User, string TenantSlug);
    private sealed record ClientLoginCandidate(Client Client, string TenantSlug);

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Base64UrlEncode(bytes);
    }

    private static string HashToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        var base64 = Convert.ToBase64String(bytes);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
