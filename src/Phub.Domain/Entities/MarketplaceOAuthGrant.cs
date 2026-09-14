using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

/// <summary>Credential/capability boundary for ML and MP applications.</summary>
public sealed class MarketplaceOAuthGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public MarketplaceProvider Provider { get; set; } = MarketplaceProvider.MercadoLivre;
    public long SellerId { get; set; }
    public string AppFamily { get; set; } = "MERCADO_LIVRE";
    public string ClientIdFingerprint { get; set; } = string.Empty;
    public string AccessTokenProtected { get; set; } = string.Empty;
    public string RefreshTokenProtected { get; set; } = string.Empty;
    public DateTimeOffset TokenExpiresAt { get; set; }
    public string ScopesJson { get; set; } = "[]";
    public string CapabilitiesJson { get; set; } = "{}";
    public DateTimeOffset? LastCapabilityVerifiedAt { get; set; }
    public string? CapabilityError { get; set; }
    public bool RequiresReauthorization { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
