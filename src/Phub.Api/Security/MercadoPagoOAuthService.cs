using System.Security.Cryptography;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Phub.Application.Abstractions;
using Phub.Application.Options;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Api.Security;

public sealed class MercadoPagoOAuthService
{
    private const string AppFamily = "MERCADO_PAGO";
    private readonly IAppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDataProtector _protector;
    private readonly MercadoPagoOptions _options;

    public MercadoPagoOAuthService(
        IAppDbContext db,
        IHttpClientFactory httpClientFactory,
        IDataProtectionProvider dataProtectionProvider,
        IOptions<MercadoPagoOptions> options)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _protector = dataProtectionProvider.CreateProtector("PrometheusHUB.MercadoPagoOAuthGrant.v1");
        _options = options.Value;
    }

    public bool IsConfigured(out string message)
    {
        if (IsMissing(_options.ClientId) || IsMissing(_options.ClientSecret) || IsMissing(_options.RedirectUri))
        {
            message = "Mercado Pago OAuth ainda nao esta configurado no ambiente da API.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    public string BuildConnectUrl(string state)
    {
        var baseUrl = _options.AuthBaseUrl.TrimEnd('/');
        return $"{baseUrl}/authorization?response_type=code" +
               $"&client_id={Uri.EscapeDataString(_options.ClientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(_options.RedirectUri)}" +
               "&platform_id=mp" +
               $"&state={Uri.EscapeDataString(state)}";
    }

    public async Task<MercadoPagoGrantResult> HandleCallbackAsync(
        string tenantId,
        Guid clientId,
        string code,
        CancellationToken cancellationToken)
    {
        var clientExists = await _db.Clients.AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId && x.Id == clientId, cancellationToken);
        if (!clientExists)
            throw new InvalidOperationException("CLIENT_NOT_FOUND");

        var token = await ExchangeCodeAsync(code, cancellationToken);
        if (token.UserId <= 0)
            throw new InvalidOperationException("MP_SELLER_NOT_RESOLVED");

        var linkedSeller = await _db.TenantMarketplaceConnections.AsNoTracking().AnyAsync(x =>
            x.TenantId == tenantId && x.ClientId == clientId &&
            x.Provider == MarketplaceProvider.MercadoLivre && x.SellerId == token.UserId,
            cancellationToken);
        if (!linkedSeller)
            throw new InvalidOperationException("MP_SELLER_MISMATCH");

        var now = DateTimeOffset.UtcNow;
        var grant = await _db.MarketplaceOAuthGrants.FirstOrDefaultAsync(x =>
            x.TenantId == tenantId && x.ClientId == clientId &&
            x.Provider == MarketplaceProvider.MercadoLivre && x.SellerId == token.UserId &&
            x.AppFamily == AppFamily, cancellationToken);
        var isNewGrant = grant == null;

        var scopes = ParseScopes(token.Scope);
        grant ??= new MarketplaceOAuthGrant
        {
            TenantId = tenantId,
            ClientId = clientId,
            Provider = MarketplaceProvider.MercadoLivre,
            SellerId = token.UserId,
            AppFamily = AppFamily,
            CreatedAt = now
        };
        if (isNewGrant)
            _db.MarketplaceOAuthGrants.Add(grant);

        grant.ClientIdFingerprint = Fingerprint(_options.ClientId);
        grant.AccessTokenProtected = _protector.Protect(token.AccessToken);
        grant.RefreshTokenProtected = string.IsNullOrWhiteSpace(token.RefreshToken)
            ? string.Empty
            : _protector.Protect(token.RefreshToken);
        grant.TokenExpiresAt = now.AddSeconds(Math.Max(60, token.ExpiresIn));
        grant.ScopesJson = JsonSerializer.Serialize(scopes);
        grant.CapabilitiesJson = JsonSerializer.Serialize(new
        {
            // Authorization alone does not prove access to the Billing resources.
            billingMercadoPago = false,
            oauth = true,
            verifiedSellerIdentity = true
        });
        grant.LastCapabilityVerifiedAt = null;
        grant.CapabilityError = null;
        grant.RequiresReauthorization = false;
        grant.UpdatedAt = now;

        await _db.SaveChangesAsync(cancellationToken);
        return new MercadoPagoGrantResult(token.UserId, scopes, now);
    }

    public async Task<MercadoPagoBillingProbeResult> ProbeBillingAsync(
        string tenantId, Guid clientId, long sellerId, CancellationToken cancellationToken)
    {
        var grant = await _db.MarketplaceOAuthGrants.FirstOrDefaultAsync(x =>
            x.TenantId == tenantId && x.ClientId == clientId &&
            x.Provider == MarketplaceProvider.MercadoLivre && x.SellerId == sellerId &&
            x.AppFamily == AppFamily, cancellationToken);
        if (grant == null)
            return new MercadoPagoBillingProbeResult(sellerId, false, "MP_GRANT_NOT_FOUND", null);

        string accessToken;
        try
        {
            accessToken = await GetValidAccessTokenAsync(grant, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            grant.CapabilityError = ex is MercadoPagoReauthorizationRequiredException
                ? "MP_REAUTHORIZATION_REQUIRED" : "MP_TOKEN_REFRESH_FAILED";
            grant.RequiresReauthorization = ex is MercadoPagoReauthorizationRequiredException;
            grant.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return new MercadoPagoBillingProbeResult(sellerId, false, grant.CapabilityError, null);
        }

        // Read-only, minimal Billing request. A successful OAuth grant alone is
        // not evidence that the separate MP Billing capability is available.
        var endpoint = new Uri(new Uri(_options.BillingApiBaseUrl.TrimEnd('/') + "/"),
            "billing/integration/monthly/periods?group=MP&document_type=BILL&limit=1");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        try
        {
            using var response = await _httpClientFactory.CreateClient("MercadoPagoOAuth")
                .SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                // Transient failures must not erase a previously verified capability.
                grant.CapabilityError = response.StatusCode == HttpStatusCode.TooManyRequests
                    ? "MP_BILLING_RATE_LIMITED" : "MP_BILLING_UNAVAILABLE";
                grant.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                return new MercadoPagoBillingProbeResult(sellerId, IsBillingVerified(grant.CapabilitiesJson),
                    grant.CapabilityError, grant.LastCapabilityVerifiedAt);
            }

            var verified = false;
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                try
                {
                    using var document = JsonDocument.Parse(body);
                    verified = document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.TryGetProperty("results", out var results) &&
                        results.ValueKind == JsonValueKind.Array;
                }
                catch (JsonException) { }
            }

            var checkedAt = DateTimeOffset.UtcNow;
            grant.CapabilitiesJson = JsonSerializer.Serialize(new
            {
                billingMercadoPago = verified,
                oauth = true,
                verifiedSellerIdentity = true
            });
            grant.LastCapabilityVerifiedAt = checkedAt;
            grant.CapabilityError = verified ? null : $"MP_BILLING_HTTP_{(int)response.StatusCode}";
            grant.UpdatedAt = checkedAt;
            await _db.SaveChangesAsync(cancellationToken);
            return new MercadoPagoBillingProbeResult(sellerId, verified, grant.CapabilityError, checkedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            grant.CapabilityError = "MP_BILLING_PROBE_FAILED";
            grant.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return new MercadoPagoBillingProbeResult(sellerId, IsBillingVerified(grant.CapabilitiesJson),
                grant.CapabilityError, grant.LastCapabilityVerifiedAt);
        }
    }

    private async Task<string> GetValidAccessTokenAsync(MarketplaceOAuthGrant grant, CancellationToken cancellationToken)
    {
        if (grant.RequiresReauthorization)
            throw new MercadoPagoReauthorizationRequiredException();
        if (grant.TokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            return _protector.Unprotect(grant.AccessTokenProtected);
        if (string.IsNullOrWhiteSpace(grant.RefreshTokenProtected))
            throw new MercadoPagoReauthorizationRequiredException();

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["refresh_token"] = _protector.Unprotect(grant.RefreshTokenProtected)
            })
        };
        using var response = await _httpClientFactory.CreateClient("MercadoPagoOAuth")
            .SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            throw new MercadoPagoReauthorizationRequiredException();
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var userId = ReadLong(root, "user_id");
        if (userId != grant.SellerId)
            throw new MercadoPagoReauthorizationRequiredException();
        var accessToken = ReadRequiredString(root, "access_token");
        var refreshToken = ReadOptionalString(root, "refresh_token");
        grant.AccessTokenProtected = _protector.Protect(accessToken);
        if (!string.IsNullOrWhiteSpace(refreshToken))
            grant.RefreshTokenProtected = _protector.Protect(refreshToken);
        grant.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, ReadLong(root, "expires_in")));
        grant.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return accessToken;
    }

    private static bool IsBillingVerified(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("billingMercadoPago", out var value) &&
                value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private async Task<MercadoPagoTokenResponse> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["code"] = code,
                ["redirect_uri"] = _options.RedirectUri
            })
        };
        using var response = await _httpClientFactory.CreateClient("MercadoPagoOAuth")
            .SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        return new MercadoPagoTokenResponse(
            ReadRequiredString(root, "access_token"),
            ReadOptionalString(root, "refresh_token"),
            ReadLong(root, "user_id"),
            (int)Math.Max(60, ReadLong(root, "expires_in")),
            ReadOptionalString(root, "scope"));
    }

    private static string ReadRequiredString(JsonElement root, string property)
        => ReadOptionalString(root, property) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"MP_OAUTH_MISSING_{property.ToUpperInvariant()}");

    private static string? ReadOptionalString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var node)) return null;
        return node.ValueKind == JsonValueKind.String ? node.GetString() : node.GetRawText().Trim('"');
    }

    private static long ReadLong(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var node)) return 0;
        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var value)) return value;
        return long.TryParse(node.GetString(), out value) ? value : 0;
    }

    private static IReadOnlyList<string> ParseScopes(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();

    private static string Fingerprint(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool IsMissing(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("__SET_VIA_", StringComparison.OrdinalIgnoreCase);

    private sealed record MercadoPagoTokenResponse(
        string AccessToken,
        string? RefreshToken,
        long UserId,
        int ExpiresIn,
        string? Scope);
}

public sealed record MercadoPagoGrantResult(long SellerId, IReadOnlyList<string> Scopes, DateTimeOffset ConnectedAt);
public sealed record MercadoPagoBillingProbeResult(long SellerId, bool Verified, string? ErrorCode, DateTimeOffset? CheckedAt);
internal sealed class MercadoPagoReauthorizationRequiredException : Exception { }
