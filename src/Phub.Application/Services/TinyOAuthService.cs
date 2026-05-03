using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Options;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class TinyOAuthService
{
    private readonly IAppDbContext _dbContext;
    private readonly ITinyErpApiClient _tinyApiClient;
    private readonly TinyErpOptions _options;
    private readonly ILogger<TinyOAuthService> _logger;

    public TinyOAuthService(
        IAppDbContext dbContext,
        ITinyErpApiClient tinyApiClient,
        IOptions<TinyErpOptions> options,
        ILogger<TinyOAuthService> logger)
    {
        _dbContext = dbContext;
        _tinyApiClient = tinyApiClient;
        _options = options.Value;
        _logger = logger;
    }

    public string BuildConnectUrl(string state)
    {
        var baseUrl = _options.AuthBaseUrl.TrimEnd('/');
        return $"{baseUrl}/auth?response_type=code" +
               $"&client_id={Uri.EscapeDataString(_options.ClientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(_options.RedirectUri)}" +
               $"&state={Uri.EscapeDataString(state)}";
    }

    public bool IsAppConfigured(out string message)
    {
        if (LooksLikePlaceholder(_options.ClientId) ||
            LooksLikePlaceholder(_options.ClientSecret) ||
            LooksLikePlaceholder(_options.RedirectUri))
        {
            message = "Tiny ERP app is not configured. Configure ClientId, ClientSecret and RedirectUri via user-secrets/env.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    public async Task<ServiceResult<TenantMarketplaceConnection>> HandleCallbackAsync(
        string tenantId,
        Guid clientId,
        string code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return ServiceResult<TenantMarketplaceConnection>.Failure(new[]
            {
                new ValidationError("tenantId", "TenantId is required")
            });
        }

        if (clientId == Guid.Empty)
        {
            return ServiceResult<TenantMarketplaceConnection>.Failure(new[]
            {
                new ValidationError("clientId", "ClientId is required")
            });
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return ServiceResult<TenantMarketplaceConnection>.Failure(new[]
            {
                new ValidationError("code", "OAuth code is required")
            });
        }

        var clientExists = await _dbContext.Clients.AnyAsync(
            item => item.TenantId == tenantId && item.Id == clientId,
            cancellationToken);
        if (!clientExists)
        {
            return ServiceResult<TenantMarketplaceConnection>.Failure(new[]
            {
                new ValidationError("clientId", "Client not found in tenant")
            });
        }

        TinyTokenResponse token;
        TinyUserInfoResult userInfo;
        try
        {
            token = await _tinyApiClient.ExchangeCodeAsync(code, _options.RedirectUri, cancellationToken);
            userInfo = await _tinyApiClient.GetUserInfoAsync(token.AccessToken, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "TinyERP OAuth exchange failed");
            return ServiceResult<TenantMarketplaceConnection>.Failure(new[]
            {
                new ValidationError("code", "Failed to exchange OAuth code with Tiny ERP")
            });
        }

        if (userInfo.Id == 0)
        {
            return ServiceResult<TenantMarketplaceConnection>.Failure(new[]
            {
                new ValidationError("userId", "User ID not resolved from Tiny ERP")
            });
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var expiresAt = nowUtc.AddSeconds(Math.Max(60, token.ExpiresIn));

        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.TinyErp
                    && item.SellerId == userInfo.Id,
            cancellationToken);

        if (connection == null)
        {
            connection = new TenantMarketplaceConnection
            {
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.TinyErp,
                SellerId = userInfo.Id,
                Nickname = userInfo.Nome,
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken,
                TokenExpiresAt = expiresAt
            };
            _dbContext.TenantMarketplaceConnections.Add(connection);
        }
        else
        {
            connection.Nickname = userInfo.Nome;
            connection.AccessToken = token.AccessToken;
            connection.RefreshToken = token.RefreshToken;
            connection.TokenExpiresAt = expiresAt;
            connection.UpdatedAt = nowUtc;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<TenantMarketplaceConnection>.Success(connection);
    }

    public async Task<string> GetValidAccessTokenAsync(
        TenantMarketplaceConnection connection,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        if (!forceRefresh &&
            connection.TokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1) &&
            !string.IsNullOrWhiteSpace(connection.AccessToken))
        {
            return connection.AccessToken;
        }

        return await RefreshAccessTokenAsync(connection, cancellationToken);
    }

    public async Task<string> RefreshAccessTokenAsync(
        TenantMarketplaceConnection connection,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connection.RefreshToken))
        {
            throw new InvalidOperationException("TINY_AUTH_INVALID");
        }

        try
        {
            var refreshed = await _tinyApiClient.RefreshTokenAsync(connection.RefreshToken, cancellationToken);
            if (string.IsNullOrWhiteSpace(refreshed.AccessToken))
            {
                throw new InvalidOperationException("TINY_AUTH_INVALID");
            }

            connection.AccessToken = refreshed.AccessToken;
            connection.RefreshToken = string.IsNullOrWhiteSpace(refreshed.RefreshToken)
                ? connection.RefreshToken
                : refreshed.RefreshToken;
            connection.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, refreshed.ExpiresIn));
            connection.UpdatedAt = DateTimeOffset.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);
            return connection.AccessToken;
        }
        catch (HttpRequestException ex) when (IsAuthRefreshFailure(ex.StatusCode))
        {
            throw new InvalidOperationException("TINY_AUTH_INVALID", ex);
        }
    }

    private static bool IsAuthRefreshFailure(HttpStatusCode? statusCode)
    {
        return statusCode == HttpStatusCode.BadRequest
               || statusCode == HttpStatusCode.Unauthorized
               || statusCode == HttpStatusCode.Forbidden;
    }

    private static bool LooksLikePlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim();
        return normalized.StartsWith("__SET_VIA_", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("<", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(">", StringComparison.OrdinalIgnoreCase);
    }
}
