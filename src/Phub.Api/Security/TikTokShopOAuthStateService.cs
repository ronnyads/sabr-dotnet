using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Phub.Api.Security;

public sealed class TikTokShopOAuthStateService
{
    private const string Purpose = "sabr:tiktokshop:oauth-state:v1";
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector;

    public TikTokShopOAuthStateService(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }

    public string CreateState(string tenantId, Guid clientId, string? returnUrl)
    {
        var payload = new TikTokShopOAuthStatePayload
        {
            TenantId = tenantId.Trim(),
            ClientId = clientId,
            ReturnUrl = NormalizeReturnUrl(returnUrl),
            IssuedAtUtc = DateTimeOffset.UtcNow,
            Nonce = Guid.NewGuid().ToString("N")
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var protectedPayload = _protector.Protect(json);
        return Base64UrlEncode(protectedPayload);
    }

    public bool TryReadState(string state, out TikTokShopOAuthStatePayload payload)
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
            var parsed = JsonSerializer.Deserialize<TikTokShopOAuthStatePayload>(unprotected, JsonOptions);
            if (parsed == null)
            {
                return false;
            }

            if (parsed.ClientId == Guid.Empty || string.IsNullOrWhiteSpace(parsed.TenantId))
            {
                return false;
            }

            if (DateTimeOffset.UtcNow - parsed.IssuedAtUtc > StateTtl)
            {
                return false;
            }

            parsed.ReturnUrl = NormalizeReturnUrl(parsed.ReturnUrl);
            payload = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string NormalizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return "/client/integrations/tiktokshop";
        }

        var candidate = returnUrl.Trim();
        if (!candidate.StartsWith("/", StringComparison.Ordinal))
        {
            return "/client/integrations/tiktokshop";
        }

        if (candidate.StartsWith("//", StringComparison.Ordinal))
        {
            return "/client/integrations/tiktokshop";
        }

        if (!candidate.StartsWith("/client/", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidate, "/client", StringComparison.OrdinalIgnoreCase))
        {
            return "/client/integrations/tiktokshop";
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

public sealed class TikTokShopOAuthStatePayload
{
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public string ReturnUrl { get; set; } = "/client/integrations/tiktokshop";
    public DateTimeOffset IssuedAtUtc { get; set; }
    public string Nonce { get; set; } = string.Empty;
}
