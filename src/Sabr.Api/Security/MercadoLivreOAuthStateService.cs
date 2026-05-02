using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Sabr.Api.Security;

public sealed class MercadoLivreOAuthStateService
{
    private const string Purpose = "sabr:mercadolivre:oauth-state:v1";
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector;
    private readonly ILogger<MercadoLivreOAuthStateService> _logger;

    public MercadoLivreOAuthStateService(
        IDataProtectionProvider dataProtectionProvider,
        ILogger<MercadoLivreOAuthStateService> logger)
    {
        _protector = dataProtectionProvider.CreateProtector(Purpose);
        _logger = logger;
    }

    public string CreateState(string tenantId, Guid clientId, string? returnUrl)
    {
        var payload = new MercadoLivreOAuthStatePayload
        {
            TenantId = tenantId.Trim(),
            ClientId = clientId,
            ReturnUrl = NormalizeReturnUrl(returnUrl),
            IssuedAtUtc = DateTimeOffset.UtcNow,
            Nonce = Guid.NewGuid().ToString("N")
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var protectedPayload = _protector.Protect(json);
        var state = Base64UrlEncode(protectedPayload);
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
            return false;
        }

        try
        {
            var raw = Base64UrlDecode(state);
            var unprotected = _protector.Unprotect(raw);
            var parsed = JsonSerializer.Deserialize<MercadoLivreOAuthStatePayload>(unprotected, JsonOptions);
            if (parsed == null)
            {
                _logger.LogWarning("ML OAuth state deserialized to null. statePrefix={Prefix}", state[..Math.Min(20, state.Length)]);
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
                "ML OAuth TryReadState failed to unprotect/parse. stateLen={Len} statePrefix={Prefix} exType={ExType}",
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

    private static string Base64UrlEncode(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }

        var bytes = Convert.FromBase64String(padded);
        return Encoding.UTF8.GetString(bytes);
    }
}

public sealed class MercadoLivreOAuthStatePayload
{
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public string ReturnUrl { get; set; } = "/client/integrations/mercadolivre";
    public DateTimeOffset IssuedAtUtc { get; set; }
    public string Nonce { get; set; } = string.Empty;
}
