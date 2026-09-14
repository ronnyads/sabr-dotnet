using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class FinancialCapabilityService
{
    private readonly IAppDbContext _db;
    private readonly MercadoLivreOAuthService _oauth;
    private readonly IMercadoLivreApiClient _api;
    public FinancialCapabilityService(IAppDbContext db, MercadoLivreOAuthService oauth, IMercadoLivreApiClient api)
    { _db = db; _oauth = oauth; _api = api; }

    public async Task<IReadOnlyList<FinancialCapabilityResult>> GetAsync(string tenantId, Guid clientId, bool probe, CancellationToken ct)
    {
        var connections = await _db.TenantMarketplaceConnections.Where(x => x.TenantId == tenantId && x.ClientId == clientId
            && x.Provider == MarketplaceProvider.MercadoLivre).ToListAsync(ct);
        var grants = await _db.MarketplaceOAuthGrants.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClientId == clientId).ToListAsync(ct);
        var results = new List<FinancialCapabilityResult>();
        foreach (var connection in connections)
        {
            var result = new FinancialCapabilityResult { SellerId = connection.SellerId, Orders = true, Shipments = true, Discounts = true };
            var sellerGrants = grants.Where(x => x.SellerId == connection.SellerId).ToList();
            result.BillingMercadoLivre = sellerGrants.Any(x => x.AppFamily == "MERCADO_LIVRE" && !x.RequiresReauthorization);
            result.BillingMercadoPago = sellerGrants.Any(x => x.AppFamily == "MERCADO_PAGO" && !x.RequiresReauthorization);
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
}
