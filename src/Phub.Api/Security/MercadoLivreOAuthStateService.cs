using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phub.Application.Options;

namespace Phub.Api.Security;

// Uses HMAC-SHA256 signed tokens instead of DataProtection to avoid key
// persistence issues across Fly.io restarts. The JWT secret is stable.
public sealed class MercadoLivreOAuthStateService
{
    private const string Version = "v2";
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly byte[] _signingKey;
    private readonly ILogger<MercadoLivreOAuthStateService> _logger;

    public MercadoLivreOAuthStateService(
        IOptions<JwtOptions> jwtOptions,
        ILogger<MercadoLivreOAuthStateService> logger)
    {
        var secret = jwtOptions.Value.Key ?? jwtOptions.Value.Secret
            ?? throw new InvalidOperationException("JWT key is required for ML OAuth state signing.");
        _signingKey = Encoding.UTF8.GetBytes(secret);
        _logger = logger;
    }

    public string CreateState(string tenantId, Guid clientId, string? returnUrl)
    {
        var payload = new MercadoLivreOAuthStatePayload
        {
            Ver = Version,
            TenantId = tenantId.Trim(),
            ClientId = clientId,
            ReturnUrl = NormalizeReturnUrl(returnUrl),
            IssuedAtUtc = DateTimeOffset.UtcNow,
            Nonce = Guid.NewGuid().ToString("N")
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var b64 = Base64UrlEncode(Encoding.UTF8.GetBytes(json));
        var sig = Base64UrlEncode(ComputeHmac(b64));
        var state = $"{b64}.{sig}";

        _logger.LogInformation(
            "ML OAuth state created. tenantId={TenantId} clientId={ClientId} stateLen={Len} statePrefix={Prefix}",
            tenantId, clientId, state.Length, state[..Math.Min(20, state.Length)]);
        return state;
    }

    public bool TryReadState(string state, out MercadoLivreOAuthStatePayload payload)
    {
        payload = default!;
        if (string.IsNullOrWhiteSpace(state))
        {
            _logger.LogWarning("ML OAuth TryReadState called with null/empty state.");
            return false;
        }

        _logger.LogInformation(
            "ML OAuth TryReadState called. stateLen={Len} statePrefix={Prefix}",
            state.Length, state[..Math.Min(30, state.Length)]);

        try
        {
            var dotIndex = state.LastIndexOf('.');
            if (dotIndex < 1 || dotIndex >= state.Length - 1)
            {
                _logger.LogWarning("ML OAuth state missing signature separator. statePrefix={Prefix}",
                    state[..Math.Min(20, state.Length)]);
                return false;
            }

            var b64 = state[..dotIndex];
            var receivedSig = state[(dotIndex + 1)..];
            var expectedSig = Base64UrlEncode(ComputeHmac(b64));

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(receivedSig),
                    Encoding.UTF8.GetBytes(expectedSig)))
            {
                _logger.LogWarning("ML OAuth state HMAC mismatch. statePrefix={Prefix}",
                    state[..Math.Min(20, state.Length)]);
                return false;
            }

            var json = Encoding.UTF8.GetString(Base64UrlDecode(b64));
            var parsed = JsonSerializer.Deserialize<MercadoLivreOAuthStatePayload>(json, JsonOptions);
            if (parsed == null)
            {
                _logger.LogWarning("ML OAuth state deserialized to null.");
                return false;
            }

            if (parsed.ClientId == Guid.Empty || string.IsNullOrWhiteSpace(parsed.TenantId))
            {
                _logger.LogWarning("ML OAuth state missing clientId or tenantId.");
                return false;
            }

            var age = DateTimeOffset.UtcNow - parsed.IssuedAtUtc;
            if (age > StateTtl)
            {
                _logger.LogWarning("ML OAuth state expired. age={Age} ttl={Ttl}", age, StateTtl);
                return false;
            }

            parsed.ReturnUrl = NormalizeReturnUrl(parsed.ReturnUrl);
            payload = parsed;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ML OAuth TryReadState failed. stateLen={Len} statePrefix={Prefix} exType={ExType}",
                state.Length,
                state[..Math.Min(20, state.Length)],
                ex.GetType().Name);
            return false;
        }
    }

    public static string NormalizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return "/client/integrations/mercadolivre";
        }

        var candidate = returnUrl.Trim();
        if (!candidate.StartsWith("/", StringComparison.Ordinal))
        {
            return "/client/integrations/mercadolivre";
        }

        if (candidate.StartsWith("//", StringComparison.Ordinal))
        {
            return "/client/integrations/mercadolivre";
        }

        if (!candidate.StartsWith("/client/", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidate, "/client", StringComparison.OrdinalIgnoreCase))
        {
            return "/client/integrations/mercadolivre";
        }

        return candidate;
    }

    private byte[] ComputeHmac(string data)
    {
        using var hmac = new HMACSHA256(_signingKey);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}

public sealed class MercadoLivreOAuthStatePayload
{
    public string Ver { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public string ReturnUrl { get; set; } = "/client/integrations/mercadolivre";
    public DateTimeOffset IssuedAtUtc { get; set; }
    public string Nonce { get; set; } = string.Empty;
}
