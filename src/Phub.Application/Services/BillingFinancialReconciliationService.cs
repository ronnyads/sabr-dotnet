using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class BillingFinancialReconciliationService
{
    private readonly IAppDbContext _db;
    private readonly MercadoLivreOAuthService _oauth;
    private readonly IMercadoLivreApiClient _api;
    private readonly FinancialLedgerService _ledger;
    private readonly OperationalFinancialProjectionService _projection;

    public BillingFinancialReconciliationService(IAppDbContext db, MercadoLivreOAuthService oauth,
        IMercadoLivreApiClient api, FinancialLedgerService ledger, OperationalFinancialProjectionService projection)
    {
        _db = db; _oauth = oauth; _api = api; _ledger = ledger; _projection = projection;
    }

    public async Task<int> ReconcileOrdersAsync(string tenantId, Guid clientId, long sellerId,
        IReadOnlyCollection<string> externalOrderIds, CancellationToken ct)
    {
        var connection = await _db.TenantMarketplaceConnections.FirstOrDefaultAsync(x =>
            x.TenantId == tenantId && x.ClientId == clientId && x.Provider == MarketplaceProvider.MercadoLivre
            && x.SellerId == sellerId, ct) ?? throw new InvalidOperationException("Seller Mercado Livre não conectado.");
        var token = await _oauth.GetValidAccessTokenAsync(connection, ct);
        var response = await _api.GetBillingOrderDetailsAsync(externalOrderIds, token, ct);
        if (response.RateLimited)
            throw new BillingRateLimitedException(response.RetryAfter ?? TimeSpan.FromMinutes(5));

        var orders = await _db.MarketplaceOrders.Include(x => x.Items).Where(x =>
            x.TenantId == tenantId && x.ClientId == clientId && x.SellerId == sellerId
            && externalOrderIds.Contains(x.MlOrderId)).ToDictionaryAsync(x => x.MlOrderId, ct);
        var reconciled = 0;
        foreach (var billing in response.Orders)
        {
            if (!orders.TryGetValue(billing.OrderId, out var order)) continue;
            var heads = await (from head in _db.FinancialEconomicHeads.AsNoTracking()
                join entry in _db.MarketplaceFinancialEntries.AsNoTracking() on head.ActiveEntryId equals entry.Id
                where head.TenantId == tenantId && head.ClientId == clientId && head.SellerId == sellerId
                      && entry.MarketplaceOrderId == order.Id
                select entry).ToListAsync(ct);

            var grossHeads = heads.Where(x => x.EntryType == FinancialEntryTypes.GrossSale).ToList();
            if (billing.GrossAmountCents > 0 && grossHeads.Count == 1)
                await ConfirmExistingAsync(grossHeads[0], billing.GrossAmountCents, billing.RawJson,
                    $"BILLING:ORDER:{billing.OrderId}:GROSS", ct);

            var feeHeads = heads.Where(x => x.EntryType == FinancialEntryTypes.SaleFee).ToList();
            if (billing.SaleFeeNetCents.HasValue && feeHeads.Count == 1)
                await ConfirmExistingAsync(feeHeads[0], -Math.Abs(billing.SaleFeeNetCents.Value), billing.RawJson,
                    $"BILLING:ORDER:{billing.OrderId}:SALE_FEE", ct);

            foreach (var charge in billing.Charges)
            {
                // Sale fee has an official aggregate above; do not duplicate its components.
                if (charge.DetailSubType?.StartsWith("CV", StringComparison.OrdinalIgnoreCase) == true) continue;
                var signed = IsCredit(charge) ? Math.Abs(charge.AmountCents) : -Math.Abs(charge.AmountCents);
                var shippingHead = !string.IsNullOrWhiteSpace(charge.ShipmentId)
                    ? heads.SingleOrDefault(x => x.EntryType == FinancialEntryTypes.SellerShippingCost
                        && x.ExternalShipmentId == charge.ShipmentId) : null;
                if (shippingHead != null && signed < 0)
                {
                    await ConfirmExistingAsync(shippingHead, signed, charge.RawJson,
                        $"BILLING:DETAIL:{charge.DetailId}", ct);
                    continue;
                }
                // A shipping credit is a new compensating economic fact. It must not
                // supersede SELLER_SHIPPING_COST with a positive amount because that
                // entry type is negative-only and the original cost remains auditable.
                var entryType = shippingHead != null && signed > 0
                    ? FinancialEntryTypes.ShippingDiscountOrCompensation
                    : FinancialEntryTypes.PlatformAdjustment;
                await _ledger.AppendAsync(new AppendFinancialEntryRequest
                {
                    TenantId = tenantId, ClientId = clientId, Provider = MarketplaceProvider.MercadoLivre,
                    SellerId = sellerId, EntryType = entryType,
                    Layer = FinancialLayers.Reconciled, Status = FinancialEntryStatuses.Confirmed,
                    AmountCents = signed, CurrencyId = billing.CurrencyId,
                    EconomicKey = $"ML:{sellerId}:ADJUSTMENT:{charge.DetailId}",
                    IdempotencyKey = $"BILLING:DETAIL:{charge.DetailId}:{Hash(charge.RawJson)}",
                    MarketplaceOrderId = order.Id, ExternalOrderId = order.MlOrderId,
                    ExternalPaymentId = billing.PaymentId?.ToString(), ExternalShipmentId = charge.ShipmentId,
                    EconomicOccurredAt = charge.OccurredAt ?? order.PaidAt ?? order.ImportedAt,
                    FinancialConfirmedAt = DateTimeOffset.UtcNow, SourceEndpoint = "/billing/integration/group/ML/order/details",
                    SourceRecordId = charge.DetailId, CanonicalPayloadHash = Hash(charge.RawJson), MetadataJson = charge.RawJson
                }, ct);
            }
            await _projection.RebuildOrderStateAsync(order, ct);
            reconciled++;
        }
        if (response.Partial) throw new BillingPartialContentException();
        return reconciled;
    }

    private async Task ConfirmExistingAsync(MarketplaceFinancialEntry current, long amount, string payload,
        string idempotency, CancellationToken ct) => await _ledger.AppendAsync(new AppendFinancialEntryRequest
    {
        TenantId = current.TenantId, ClientId = current.ClientId, Provider = current.Provider, SellerId = current.SellerId,
        EntryType = current.EntryType, Layer = FinancialLayers.Reconciled, Status = FinancialEntryStatuses.Confirmed,
        AmountCents = amount, CurrencyId = current.CurrencyId, EconomicKey = current.EconomicKey,
        IdempotencyKey = $"{idempotency}:{Hash(payload)}", MarketplaceOrderId = current.MarketplaceOrderId,
        MarketplaceOrderItemId = current.MarketplaceOrderItemId, ExternalOrderId = current.ExternalOrderId,
        ExternalPaymentId = current.ExternalPaymentId, ExternalShipmentId = current.ExternalShipmentId,
        EconomicOccurredAt = current.EconomicOccurredAt, FinancialConfirmedAt = DateTimeOffset.UtcNow,
        SourceEndpoint = "/billing/integration/group/ML/order/details", SourceRecordId = idempotency,
        CanonicalPayloadHash = Hash(payload), MetadataJson = payload
    }, ct);

    private static bool IsCredit(FinancialBillingChargeDetail charge) =>
        charge.DetailType.Contains("CREDIT", StringComparison.OrdinalIgnoreCase)
        || charge.DetailType.Contains("BONUS", StringComparison.OrdinalIgnoreCase)
        || charge.Status?.StartsWith("BONUS", StringComparison.OrdinalIgnoreCase) == true
        || !string.IsNullOrWhiteSpace(charge.BonifiedChargeId);
    private static string Hash(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
}

public sealed class BillingRateLimitedException(TimeSpan retryAfter) : Exception("BILLING_RATE_LIMITED")
{ public TimeSpan RetryAfter { get; } = retryAfter; }
public sealed class BillingPartialContentException : Exception { public BillingPartialContentException() : base("BILLING_PARTIAL_CONTENT") { } }
