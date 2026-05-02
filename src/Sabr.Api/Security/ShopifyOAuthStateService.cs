using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Sabr.Api.Security;

public sealed class ShopifyOAuthStateService
{
    private const string Purpose = "sabr:shopify:oauth-state:v1";
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector;

    public ShopifyOAuthStateService(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }

    public string CreateState(string tenantId, Guid clientId, string shop, string? returnUrl)
    {
        var payload = new ShopifyOAuthStatePayload
        {
            TenantId = tenantId.Trim(),
            ClientId = clientId,
            Shop = shop.Trim().ToLowerInvariant(),
            ReturnUrl = NormalizeReturnUrl(returnUrl),
            IssuedAtUtc = DateTimeOffset.UtcNow,
            Nonce = Guid.NewGuid().ToString("N")
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var protectedPayload = _protector.Protect(json);
        return Base64UrlEncode(protectedPayload);
    }

    public bool TryReadState(string state, out ShopifyOAuthStatePayload payload)
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
            var parsed = JsonSerializer.Deserialize<ShopifyOAuthStatePayload>(unprotected, JsonOptions);
            if (parsed == null)
            {
                return false;
            }

            if (parsed.ClientId == Guid.Empty || string.IsNullOrWhiteSpace(parsed.TenantId))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(parsed.Shop))
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
            return "/client/integrations/shopify";
        }

        var candidate = returnUrl.Trim();
        if (!candidate.StartsWith("/", StringComparison.Ordinal))
        {
            return "/client/integrations/shopify";
        }

        if (candidate.StartsWith("//", StringComparison.Ordinal))
        {
            return "/client/integrations/shopify";
        }

        if (!candidate.StartsWith("/client/", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidate, "/client", StringComparison.OrdinalIgnoreCase))
        {
            return "/client/integrations/shopify";
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

public sealed class ShopifyOAuthStatePayload
{
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public string Shop { get; set; } = string.Empty;
    public string ReturnUrl { get; set; } = "/client/integrations/shopify";
    public DateTimeOffset IssuedAtUtc { get; set; }
    public string Nonce { get; set; } = string.Empty;
}
