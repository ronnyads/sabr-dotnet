using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class FinancialCapabilityService
{
    private readonly IAppDbContext _db;
    private readonly MercadoLivreOAuthService _oauth;
    private readonly IMercadoLivreApiClient _api;
    private readonly ILogger<FinancialCapabilityService>? _logger;

    public FinancialCapabilityService(
        IAppDbContext db,
        MercadoLivreOAuthService oauth,
        IMercadoLivreApiClient api,
        ILogger<FinancialCapabilityService>? logger = null)
    {
        _db = db;
        _oauth = oauth;
        _api = api;
        _logger = logger;
    }

    public async Task<IReadOnlyList<FinancialCapabilityResult>> GetAsync(string tenantId, Guid clientId, bool probe, CancellationToken ct)
    {
        var connections = await _db.TenantMarketplaceConnections.Where(x => x.TenantId == tenantId && x.ClientId == clientId
            && x.Provider == MarketplaceProvider.MercadoLivre).ToListAsync(ct);
        var grants = await _db.MarketplaceOAuthGrants.Where(x => x.TenantId == tenantId && x.ClientId == clientId).ToListAsync(ct);
        var results = new List<FinancialCapabilityResult>();
        foreach (var connection in connections)
        {
            var result = new FinancialCapabilityResult { SellerId = connection.SellerId, Orders = true, Shipments = true, Discounts = true };
            var sellerGrants = grants.Where(x => x.SellerId == connection.SellerId).ToList();
            var mlGrant = sellerGrants.FirstOrDefault(x => x.AppFamily == "MERCADO_LIVRE");
            result.BillingMercadoLivre = mlGrant != null && !mlGrant.RequiresReauthorization &&
                mlGrant.LastCapabilityVerifiedAt > DateTimeOffset.UtcNow.AddDays(-1) &&
                IsBillingVerified(mlGrant.CapabilitiesJson, "billingMercadoLivre");
            result.BillingMercadoPago = sellerGrants.Any(x => x.AppFamily == "MERCADO_PAGO" &&
                !x.RequiresReauthorization && x.TokenExpiresAt > DateTimeOffset.UtcNow &&
                x.LastCapabilityVerifiedAt.HasValue &&
                IsBillingVerified(x.CapabilitiesJson, "billingMercadoPago"));
            result.VerifiedAt = sellerGrants.MaxBy(x => x.LastCapabilityVerifiedAt)?.LastCapabilityVerifiedAt;
            if (probe)
            {
                try
                {
                    var token = await _oauth.GetValidAccessTokenAsync(connection, ct);
                    var me = await _api.GetUserMeAsync(token, ct);
                    var identityMatches = long.TryParse(me.SellerId, out var actual) && actual == connection.SellerId;
                    result.Orders = result.Shipments = result.Discounts = identityMatches;
                    result.VerifiedAt = DateTimeOffset.UtcNow;
                    if (!identityMatches) result.Pending.Add("ML_SELLER_IDENTITY_MISMATCH");
                    if (identityMatches)
                    {
                        FinancialBillingProbeResponse billing;
                        try
                        {
                            billing = await _api.ProbeBillingPeriodsAsync(token, ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            billing = new FinancialBillingProbeResponse(false, true, "ML_BILLING_PROBE_FAILED");
                        }
                        mlGrant ??= new Phub.Domain.Entities.MarketplaceOAuthGrant
                        {
                            TenantId = tenantId, ClientId = clientId, Provider = MarketplaceProvider.MercadoLivre,
                            SellerId = connection.SellerId, AppFamily = "MERCADO_LIVRE"
                        };
                        if (!sellerGrants.Contains(mlGrant))
                        {
                            _db.MarketplaceOAuthGrants.Add(mlGrant);
                            sellerGrants.Add(mlGrant);
                        }
                        if (!billing.TransientFailure)
                        {
                            mlGrant.CapabilitiesJson = JsonSerializer.Serialize(new { billingMercadoLivre = billing.Verified });
                            mlGrant.LastCapabilityVerifiedAt = DateTimeOffset.UtcNow;
                            result.BillingMercadoLivre = billing.Verified;
                        }
                        mlGrant.CapabilityError = billing.ErrorCode;
                        mlGrant.UpdatedAt = DateTimeOffset.UtcNow;
                        await _db.SaveChangesAsync(ct);
                        if (!billing.Verified)
                        {
                            _logger?.LogWarning(
                                "Mercado Livre Billing capability probe did not verify access. sellerId={SellerId} errorCode={ErrorCode} transient={TransientFailure}",
                                connection.SellerId,
                                billing.ErrorCode,
                                billing.TransientFailure);
                        }
                        if (billing.TransientFailure) result.Pending.Add(billing.ErrorCode!);
                    }
                }
                catch { result.Orders = result.Shipments = result.Discounts = false; result.Pending.Add("ML_REAUTHORIZATION_REQUIRED"); }
            }
            if (!result.BillingMercadoLivre) result.Pending.Add("BILLING_ML_GRANT_REQUIRED");
            if (!result.BillingMercadoPago) result.Pending.Add("BILLING_MP_GRANT_REQUIRED");
            result.RequiresReauthorization = result.Pending.Count > 0;
            results.Add(result);
        }
        return results;
    }

    private static bool IsBillingVerified(string? json, string capability)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(capability, out var value) &&
                value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }
}
