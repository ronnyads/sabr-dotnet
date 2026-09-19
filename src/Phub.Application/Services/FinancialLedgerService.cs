using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Domain.Entities;

namespace Phub.Application.Services;

public sealed class FinancialLedgerService
{
    private static readonly HashSet<string> PositiveOnly = new(StringComparer.Ordinal)
    {
        FinancialEntryTypes.GrossSale,
        FinancialEntryTypes.ProductCostRecovery,
        FinancialEntryTypes.ShippingDiscountOrCompensation
    };

    private static readonly HashSet<string> NegativeOnly = new(StringComparer.Ordinal)
    {
        FinancialEntryTypes.BuyerDiscount,
        FinancialEntryTypes.SaleFee,
        FinancialEntryTypes.FinancingOrFixedFee,
        FinancialEntryTypes.SellerShippingCost,
        FinancialEntryTypes.Refund,
        FinancialEntryTypes.ChargebackOrClaim,
        FinancialEntryTypes.ReturnShippingCost,
        FinancialEntryTypes.ProductCost,
        FinancialEntryTypes.SellerTaxEstimate
    };

    private readonly IAppDbContext _db;

    public FinancialLedgerService(IAppDbContext db) => _db = db;

    public async Task<MarketplaceFinancialEntry> AppendAsync(AppendFinancialEntryRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);

        var existing = await _db.MarketplaceFinancialEntries.AsNoTracking().FirstOrDefaultAsync(
            x => x.TenantId == request.TenantId && x.Provider == request.Provider && x.SellerId == request.SellerId
                 && x.IdempotencyKey == request.IdempotencyKey,
            cancellationToken);
        if (existing != null) return existing;

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        // Serialize writers for the same economic identity in PostgreSQL. The
        // lock is transaction-scoped, so a crashed writer cannot strand it.
        if (_db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
        {
            var lockIdentity = $"{request.TenantId}|{request.ClientId:N}|{request.Provider}|{request.SellerId}|{request.EconomicKey}";
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockIdentity}, 0))", cancellationToken);
        }

        // The first check is the fast path; this second check is mandatory
        // after acquiring the economic-key lock to make retries idempotent.
        existing = await _db.MarketplaceFinancialEntries.AsNoTracking().FirstOrDefaultAsync(
            x => x.TenantId == request.TenantId && x.Provider == request.Provider && x.SellerId == request.SellerId
                 && x.IdempotencyKey == request.IdempotencyKey,
            cancellationToken);
        if (existing != null)
        {
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        var head = await _db.FinancialEconomicHeads.FirstOrDefaultAsync(
            x => x.TenantId == request.TenantId && x.ClientId == request.ClientId && x.Provider == request.Provider
                 && x.SellerId == request.SellerId && x.EconomicKey == request.EconomicKey,
            cancellationToken);
        MarketplaceFinancialEntry? previous = null;
        if (head != null)
        {
            previous = await _db.MarketplaceFinancialEntries.FirstAsync(x => x.Id == head.ActiveEntryId, cancellationToken);
            EnsureSameEconomicIdentity(previous, request);
        }

        var entry = new MarketplaceFinancialEntry
        {
            TenantId = request.TenantId,
            ClientId = request.ClientId,
            Provider = request.Provider,
            SellerId = request.SellerId,
            EntryType = request.EntryType,
            Layer = request.Layer,
            Status = request.Status,
            AmountCents = request.AmountCents,
            CurrencyId = request.CurrencyId.ToUpperInvariant(),
            EconomicKey = request.EconomicKey,
            IdempotencyKey = request.IdempotencyKey,
            SupersedesEntryId = previous?.Id,
            MarketplaceOrderId = request.MarketplaceOrderId,
            MarketplaceOrderItemId = request.MarketplaceOrderItemId,
            ExternalOrderId = request.ExternalOrderId,
            ExternalPaymentId = request.ExternalPaymentId,
            ExternalShipmentId = request.ExternalShipmentId,
            ExternalPackId = request.ExternalPackId,
            ExternalClaimId = request.ExternalClaimId,
            ExternalReturnId = request.ExternalReturnId,
            // PostgreSQL timestamptz accepts UTC DateTimeOffset values only.
            // Preserve the instant while normalizing provider-local offsets.
            EconomicOccurredAt = request.EconomicOccurredAt.ToUniversalTime(),
            FinancialConfirmedAt = request.FinancialConfirmedAt?.ToUniversalTime(),
            ProviderUpdatedAt = request.ProviderUpdatedAt?.ToUniversalTime(),
            ObservedAt = DateTimeOffset.UtcNow,
            SourceEndpoint = request.SourceEndpoint,
            SourceRecordId = request.SourceRecordId,
            CanonicalPayloadHash = request.CanonicalPayloadHash,
            MetadataJson = request.MetadataJson
        };
        _db.MarketplaceFinancialEntries.Add(entry);
        await _db.SaveChangesAsync(cancellationToken);

        if (head == null)
        {
            head = new FinancialEconomicHead
            {
                TenantId = request.TenantId,
                ClientId = request.ClientId,
                Provider = request.Provider,
                SellerId = request.SellerId,
                EconomicKey = request.EconomicKey,
                ActiveEntryId = entry.Id
            };
            _db.FinancialEconomicHeads.Add(head);
        }
        else
        {
            // A chain can only advance from its current head. That makes cycles
            // impossible and guarantees a single active fact per economic key.
            head.ActiveEntryId = entry.Id;
            head.Version += 1;
            head.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return entry;
    }

    private static void Validate(AppendFinancialEntryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId) || request.ClientId == Guid.Empty || request.SellerId <= 0)
            throw new ArgumentException("Tenant, client and seller are required.");
        if (request.AmountCents == 0) throw new ArgumentException("Financial entries cannot be zero.");
        if (PositiveOnly.Contains(request.EntryType) && request.AmountCents < 0)
            throw new ArgumentException($"{request.EntryType} must be positive.");
        if (NegativeOnly.Contains(request.EntryType) && request.AmountCents > 0)
            throw new ArgumentException($"{request.EntryType} must be negative.");
        if (request.Status == FinancialEntryStatuses.Confirmed && !request.FinancialConfirmedAt.HasValue)
            throw new ArgumentException("Confirmed entries require financialConfirmedAt.");
        if (request.EconomicOccurredAt == default) throw new ArgumentException("economicOccurredAt is required.");
        if (string.IsNullOrWhiteSpace(request.EconomicKey) || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Economic and idempotency keys are required.");
        if (string.IsNullOrWhiteSpace(request.CanonicalPayloadHash) || request.CanonicalPayloadHash.Length > 64)
            throw new ArgumentException("A canonical payload hash is required.");
    }

    private static void EnsureSameEconomicIdentity(MarketplaceFinancialEntry current, AppendFinancialEntryRequest next)
    {
        if (current.TenantId != next.TenantId || current.ClientId != next.ClientId || current.Provider != next.Provider
            || current.SellerId != next.SellerId || current.EconomicKey != next.EconomicKey || current.EntryType != next.EntryType
            || !string.Equals(current.CurrencyId, next.CurrencyId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A financial entry can only supersede the current head of the same economic identity.");
    }
}
