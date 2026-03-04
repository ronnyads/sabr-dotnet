using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Options;
using Sabr.Application.Validation;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;

namespace Sabr.Application.Services;

public sealed class MercadoLivreOAuthService
{
    private readonly IAppDbContext _dbContext;
    private readonly IMercadoLivreApiClient _mercadoLivreApiClient;
    private readonly MercadoLivreOptions _options;

    public MercadoLivreOAuthService(
        IAppDbContext dbContext,
        IMercadoLivreApiClient mercadoLivreApiClient,
        IOptions<MercadoLivreOptions> options)
    {
        _dbContext = dbContext;
        _mercadoLivreApiClient = mercadoLivreApiClient;
        _options = options.Value;
    }

    public string BuildConnectUrl(string state)
    {
        var baseUrl = _options.AuthBaseUrl.TrimEnd('/');
        return $"{baseUrl}/authorization?response_type=code" +
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
            message = "Mercado Livre app is not configured. Configure ClientId, ClientSecret and RedirectUri via user-secrets/env.";
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

        var token = await _mercadoLivreApiClient.ExchangeCodeAsync(code, cancellationToken);
        var me = await _mercadoLivreApiClient.GetUserMeAsync(token.AccessToken, cancellationToken);
        if (string.IsNullOrWhiteSpace(me.SellerId))
        {
            return ServiceResult<TenantMarketplaceConnection>.Failure(new[]
            {
                new ValidationError("sellerId", "Seller not resolved from Mercado Livre")
            });
        }

        if (!MercadoLivreSellerIdParser.TryParseRequired(me.SellerId, out var sellerId))
        {
            return ServiceResult<TenantMarketplaceConnection>.Failure(new[]
            {
                new ValidationError("sellerId", "SellerId returned by Mercado Livre is invalid")
            });
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var expiresAt = nowUtc.AddSeconds(Math.Max(60, token.ExpiresInSeconds));

        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.MercadoLivre
                    && item.SellerId == sellerId,
            cancellationToken);

        if (connection == null)
        {
            connection = new TenantMarketplaceConnection
            {
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                SellerId = sellerId,
                Nickname = me.Nickname,
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken,
                TokenExpiresAt = expiresAt
            };
            _dbContext.TenantMarketplaceConnections.Add(connection);
        }
        else
        {
            connection.Nickname = me.Nickname;
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
            throw new InvalidOperationException("ML_AUTH_INVALID");
        }

        try
        {
            var refreshed = await _mercadoLivreApiClient.RefreshTokenAsync(connection.RefreshToken, cancellationToken);
            if (string.IsNullOrWhiteSpace(refreshed.AccessToken))
            {
                throw new InvalidOperationException("ML_AUTH_INVALID");
            }

            connection.AccessToken = refreshed.AccessToken;
            connection.RefreshToken = string.IsNullOrWhiteSpace(refreshed.RefreshToken)
                ? connection.RefreshToken
                : refreshed.RefreshToken;
            connection.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, refreshed.ExpiresInSeconds));
            connection.UpdatedAt = DateTimeOffset.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);
            return connection.AccessToken;
        }
        catch (HttpRequestException ex) when (IsAuthRefreshFailure(ex.StatusCode))
        {
            throw new InvalidOperationException("ML_AUTH_INVALID", ex);
        }
        catch (InvalidOperationException ex) when (string.Equals(ex.Message, "invalid_grant", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ML_AUTH_INVALID", ex);
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
