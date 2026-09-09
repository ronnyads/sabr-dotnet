using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Options;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

/// <summary>
/// Confirma o pagamento interno como uma unica unidade financeira e operacional.
/// Nenhuma chamada remota acontece enquanto os locks de banco estao mantidos.
/// </summary>
public sealed class MarketplaceOrderCheckoutService
{
    private readonly IAppDbContext _db;
    private readonly MarketplaceOrderInventoryService _inventory;
    private readonly StockAvailabilityService _stockAvailability;
    private readonly MarketplaceAuditLogService _audit;
    private readonly MercadoLivreOptions _options;

    public MarketplaceOrderCheckoutService(
        IAppDbContext db,
        MarketplaceOrderInventoryService inventory,
        StockAvailabilityService stockAvailability,
        MarketplaceAuditLogService audit,
        IOptions<MercadoLivreOptions> options)
    {
        _db = db;
        _inventory = inventory;
        _stockAvailability = stockAvailability;
        _audit = audit;
        _options = options.Value;
    }

    public async Task<ServiceResult<MarketplaceOrderPaymentQuoteResult>> GetQuoteAsync(
        string tenantId,
        Guid clientId,
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        if (!IsContextValid(tenantId, clientId, orderId))
            return Failure<MarketplaceOrderPaymentQuoteResult>("context", "INVALID_PAYMENT_CONTEXT");

        var order = await _db.MarketplaceOrders
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == orderId
                                         && item.TenantId == tenantId
                                         && item.ClientId == clientId,
                cancellationToken);
        if (order == null)
            return Failure<MarketplaceOrderPaymentQuoteResult>("orderId", "ORDER_NOT_FOUND");

        var items = await _db.MarketplaceOrderItems
            .AsNoTracking()
            .Where(item => item.MarketplaceOrderId == order.Id)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var shipments = await LoadShipmentsAsync(order, cancellationToken);
        var variants = await LoadVariantsAsync(items, tracking: false, lockForUpdate: false, cancellationToken);
        var walletBalance = await _db.WalletAccounts
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.ClientId == clientId)
            .Select(item => (long?)item.BalanceCents)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;

        var inventorySummary = MarketplaceOrderInventoryService.BuildSummary(
            order,
            shipments,
            await _inventory.BuildItemSummariesAsync(items, cancellationToken));
        var quote = BuildQuote(order, items, variants, walletBalance, inventorySummary.PaymentBlockers);
        return ServiceResult<MarketplaceOrderPaymentQuoteResult>.Success(quote);
    }

    public async Task<ServiceResult<MarketplaceMarkPaidExecutionResult>> ConfirmAsync(
        string tenantId,
        Guid clientId,
        Guid orderId,
        bool force,
        string? expectedQuoteHash,
        CancellationToken cancellationToken = default)
    {
        if (!IsContextValid(tenantId, clientId, orderId))
            return Failure<MarketplaceMarkPaidExecutionResult>("context", "INVALID_PAYMENT_CONTEXT");

        // Reconciliacao pode criar a reserva. Ela termina antes do checkout para manter
        // a transacao financeira curta e sem chamadas externas.
        var orderForReconciliation = await _db.MarketplaceOrders
            .Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.Id == orderId
                                         && item.TenantId == tenantId
                                         && item.ClientId == clientId,
                cancellationToken);
        if (orderForReconciliation == null)
            return Failure<MarketplaceMarkPaidExecutionResult>("orderId", "ORDER_NOT_FOUND");

        // Um retry idempotente nunca pode recriar reservas de um pedido que ja foi
        // confirmado. A verificacao definitiva ainda ocorre sob lock abaixo.
        if (!orderForReconciliation.SabrPaymentConfirmedAt.HasValue
            && orderForReconciliation.Items.Count > 0
            && orderForReconciliation.Items.All(item => !MarketplaceMappingStates.IsUnmapped(item.MappingState)))
        {
            await _inventory.ReconcileReservationsAsync(
                orderForReconciliation,
                orderForReconciliation.SellerId,
                reservationTtlHours: 24,
                cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }

        ((DbContext)_db).ChangeTracker.Clear();
        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
            transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var changedSkus = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var order = await LoadOrderForUpdateAsync(tenantId, clientId, orderId, cancellationToken);
            if (order == null)
                return await RollbackFailureAsync<MarketplaceMarkPaidExecutionResult>(transaction, "orderId", "ORDER_NOT_FOUND", cancellationToken);

            if (order.SabrPaymentConfirmedAt.HasValue)
            {
                if (transaction != null)
                    await transaction.CommitAsync(cancellationToken);
                var balanceAfter = order.WalletLedgerEntryId.HasValue
                    ? await _db.WalletLedgerEntries.AsNoTracking()
                        .Where(item => item.Id == order.WalletLedgerEntryId.Value)
                        .Select(item => (long?)item.BalanceAfterCents)
                        .SingleOrDefaultAsync(cancellationToken)
                    : null;
                return ServiceResult<MarketplaceMarkPaidExecutionResult>.Success(new MarketplaceMarkPaidExecutionResult
                {
                    Result = new MarketplaceMarkPaidResult
                    {
                        OrderId = order.Id,
                        AlreadyPaid = true,
                        SabrPaymentConfirmedAt = order.SabrPaymentConfirmedAt,
                        RiskFlagsJson = order.RiskFlagsJson,
                        TotalChargedCents = order.TotalChargeCentsAtPayment,
                        WalletBalanceAfterCents = balanceAfter,
                        WalletLedgerEntryId = order.WalletLedgerEntryId
                    }
                });
            }

            var items = await _db.MarketplaceOrderItems
                .Where(item => item.MarketplaceOrderId == order.Id)
                .OrderBy(item => item.Id)
                .ToListAsync(cancellationToken);
            var shipments = await LoadShipmentsAsync(order, cancellationToken);
            var variants = await LoadVariantsAsync(items, tracking: true, lockForUpdate: true, cancellationToken);
            var inventorySummary = MarketplaceOrderInventoryService.BuildSummary(
                order,
                shipments,
                await _inventory.BuildItemSummariesAsync(items, cancellationToken));

            var wallet = await LoadWalletForUpdateAsync(tenantId, clientId, cancellationToken);
            var quote = BuildQuote(order, items, variants, wallet?.BalanceCents ?? 0, inventorySummary.PaymentBlockers);
            var blockerError = ResolveBlockerError(quote.PaymentBlockers);
            if (blockerError != null)
                return await RollbackFailureAsync<MarketplaceMarkPaidExecutionResult>(transaction, blockerError.Value.Field, blockerError.Value.Code, cancellationToken);

            if (!string.IsNullOrWhiteSpace(expectedQuoteHash)
                && !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expectedQuoteHash.Trim().ToUpperInvariant()),
                    Encoding.ASCII.GetBytes(quote.QuoteHash)))
            {
                return await RollbackFailureAsync<MarketplaceMarkPaidExecutionResult>(transaction, "quote", "PAYMENT_QUOTE_CHANGED", cancellationToken);
            }

            var nowUtc = DateTimeOffset.UtcNow;
            var risk = await EvaluateRiskAsync(order, nowUtc, cancellationToken);
            if (risk.Reasons.Count > 0 && !force)
            {
                if (transaction != null)
                    await transaction.RollbackAsync(cancellationToken);
                return ServiceResult<MarketplaceMarkPaidExecutionResult>.Success(new MarketplaceMarkPaidExecutionResult
                {
                    ConfirmationRequired = true,
                    Confirmation = risk.Confirmation
                });
            }

            if (wallet == null || wallet.BalanceCents < quote.TotalChargeCents)
                return await RollbackFailureAsync<MarketplaceMarkPaidExecutionResult>(transaction, "balance", "INSUFFICIENT_WALLET_BALANCE", cancellationToken);

            var reservations = await _db.StockReservations
                .Where(item => item.MarketplaceOrderId == order.Id
                               && item.TenantId == tenantId
                               && item.ClientId == clientId
                               && item.Status == StockReservationStatus.Reserved)
                .OrderBy(item => item.ReservedAt)
                .ToListAsync(cancellationToken);
            ConsumeReservations(items, reservations, variants, changedSkus, nowUtc);

            // ReservedStock e uma projecao. Reconstroi a partir das reservas ativas
            // de outros pedidos para reparar contadores antigos sem liberar estoque alheio.
            foreach (var sku in changedSkus.OrderBy(item => item, StringComparer.Ordinal))
            {
                var reservedForOtherOrders = await _db.StockReservations
                    .AsNoTracking()
                    .Where(item => item.SabrVariantSku == sku
                                   && item.MarketplaceOrderId != order.Id
                                   && item.Status == StockReservationStatus.Reserved)
                    .SumAsync(item => item.Quantity, cancellationToken);
                variants[sku].ReservedStock = Math.Max(0, reservedForOtherOrders);
                variants[sku].AvailableStock = StockAvailabilityService.ComputeAvailable(variants[sku]);
            }

            foreach (var item in items)
            {
                var variant = variants[item.SabrVariantSku!];
                item.CatalogUnitPriceCentsAtPayment = variant.CatalogPriceCents;
                item.CostUnitPriceCentsAtPayment = variant.CostPriceCents;
                item.ChargeLineTotalCentsAtPayment = checked(variant.CatalogPriceCents * item.Quantity);
            }

            wallet.BalanceCents -= quote.TotalChargeCents;
            wallet.UpdatedAt = nowUtc;
            var ledger = new WalletLedgerEntry
            {
                TenantId = tenantId,
                ClientId = clientId,
                OrderId = order.Id,
                Type = WalletEntryType.Debit,
                AmountCents = quote.TotalChargeCents,
                BalanceAfterCents = wallet.BalanceCents,
                RequestId = Guid.NewGuid(),
                ReferenceType = "MarketplaceOrder",
                ReferenceId = order.Id.ToString("D")
            };
            _db.WalletLedgerEntries.Add(ledger);

            order.ProductSubtotalCentsAtPayment = quote.ProductSubtotalCents;
            order.FreightCentsAtPayment = quote.FreightCents;
            order.AdditionalCentsAtPayment = quote.AdditionalCents;
            order.DiscountCentsAtPayment = quote.DiscountCents;
            order.TotalChargeCentsAtPayment = quote.TotalChargeCents;
            order.PaymentQuoteHash = quote.QuoteHash;
            order.WalletLedgerEntryId = ledger.Id;
            order.SabrPaymentConfirmedAt = nowUtc;
            order.PaidAt ??= nowUtc;
            if (risk.Reasons.Count > 0)
                order.RiskFlagsJson = JsonSerializer.Serialize(new { reasons = risk.Reasons });

            await _audit.RecordAsync(
                order.TenantId,
                order.ClientId,
                order.Provider,
                order.SellerId,
                MarketplaceEventTopics.AuditOrderPaid,
                order.MlOrderId,
                new
                {
                    orderId = order.MlOrderId,
                    paidAt = order.SabrPaymentConfirmedAt,
                    totalChargeCents = quote.TotalChargeCents,
                    walletLedgerEntryId = ledger.Id,
                    quoteHash = quote.QuoteHash,
                    risk = order.RiskFlagsJson
                },
                "checkout-v2",
                cancellationToken);

            await _db.SaveChangesAsync(cancellationToken);
            if (transaction != null)
                await transaction.CommitAsync(cancellationToken);

            if (changedSkus.Count > 0)
                await _stockAvailability.SyncStockForSkusAsync(tenantId, clientId, changedSkus, cancellationToken);

            return ServiceResult<MarketplaceMarkPaidExecutionResult>.Success(new MarketplaceMarkPaidExecutionResult
            {
                Result = new MarketplaceMarkPaidResult
                {
                    OrderId = order.Id,
                    AlreadyPaid = false,
                    SabrPaymentConfirmedAt = order.SabrPaymentConfirmedAt,
                    RiskFlagsJson = order.RiskFlagsJson,
                    TotalChargedCents = quote.TotalChargeCents,
                    WalletBalanceAfterCents = wallet.BalanceCents,
                    WalletLedgerEntryId = ledger.Id
                }
            });
        }
        catch
        {
            if (transaction != null)
                await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (transaction != null)
                await transaction.DisposeAsync();
        }
    }

    private async Task<MarketplaceOrder?> LoadOrderForUpdateAsync(string tenantId, Guid clientId, Guid orderId, CancellationToken cancellationToken)
    {
        if (IsNpgsql())
        {
            return await _db.MarketplaceOrders
                .FromSqlInterpolated($"SELECT * FROM marketplace_orders WHERE id = {orderId} AND tenant_id = {tenantId} AND client_id = {clientId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
        }

        return await _db.MarketplaceOrders.SingleOrDefaultAsync(
            item => item.Id == orderId && item.TenantId == tenantId && item.ClientId == clientId,
            cancellationToken);
    }

    private async Task<WalletAccount?> LoadWalletForUpdateAsync(string tenantId, Guid clientId, CancellationToken cancellationToken)
    {
        if (IsNpgsql())
        {
            return await _db.WalletAccounts
                .FromSqlInterpolated($"SELECT * FROM wallet_accounts WHERE tenant_id = {tenantId} AND client_id = {clientId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
        }

        return await _db.WalletAccounts.SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.ClientId == clientId,
            cancellationToken);
    }

    private async Task<Dictionary<string, ProductVariant>> LoadVariantsAsync(
        IReadOnlyCollection<MarketplaceOrderItem> items,
        bool tracking,
        bool lockForUpdate,
        CancellationToken cancellationToken)
    {
        var skus = items
            .Where(item => !string.IsNullOrWhiteSpace(item.SabrVariantSku))
            .Select(item => item.SabrVariantSku!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        if (skus.Length == 0)
            return new Dictionary<string, ProductVariant>(StringComparer.Ordinal);

        if (lockForUpdate && IsNpgsql())
        {
            await _db.ProductVariants
                .FromSqlRaw("SELECT * FROM product_variants WHERE variant_sku = ANY ({0}) ORDER BY variant_sku FOR UPDATE", skus)
                .LoadAsync(cancellationToken);
        }

        IQueryable<ProductVariant> query = _db.ProductVariants.Where(item => skus.Contains(item.VariantSku));
        if (!tracking)
            query = query.AsNoTracking();
        return await query.ToDictionaryAsync(item => item.VariantSku, StringComparer.Ordinal, cancellationToken);
    }

    private async Task<List<MarketplaceShipment>> LoadShipmentsAsync(MarketplaceOrder order, CancellationToken cancellationToken) =>
        await _db.MarketplaceShipments.AsNoTracking()
            .Where(item => item.TenantId == order.TenantId
                           && item.ClientId == order.ClientId
                           && item.Provider == order.Provider
                           && item.MlOrderId == order.MlOrderId)
            .ToListAsync(cancellationToken);

    private static MarketplaceOrderPaymentQuoteResult BuildQuote(
        MarketplaceOrder order,
        IReadOnlyCollection<MarketplaceOrderItem> items,
        IReadOnlyDictionary<string, ProductVariant> variants,
        long walletBalance,
        IReadOnlyCollection<string> inventoryBlockers)
    {
        var blockers = new HashSet<string>(inventoryBlockers, StringComparer.Ordinal);
        var quoteItems = new List<MarketplaceOrderPaymentQuoteItemResult>();
        long subtotal = 0;

        foreach (var item in items.OrderBy(entry => entry.Id))
        {
            if (string.IsNullOrWhiteSpace(item.SabrVariantSku)
                || !variants.TryGetValue(item.SabrVariantSku, out var variant))
                continue;

            if (variant.CatalogPriceCents <= 0)
                blockers.Add(MarketplaceOrderPaymentBlockers.PricingMissing);

            var unitPrice = Math.Max(0, variant.CatalogPriceCents);
            var lineTotal = checked(unitPrice * Math.Max(0, item.Quantity));
            subtotal = checked(subtotal + lineTotal);
            quoteItems.Add(new MarketplaceOrderPaymentQuoteItemResult
            {
                OrderItemId = item.Id,
                Sku = variant.VariantSku,
                ProductName = string.IsNullOrWhiteSpace(item.ProductName) ? variant.Name : item.ProductName,
                Quantity = item.Quantity,
                UnitPriceCents = unitPrice,
                LineTotalCents = lineTotal
            });
        }

        const long freight = 0;
        const long additional = 0;
        const long discount = 0;
        var total = checked(subtotal + freight + additional - discount);
        if (walletBalance < total)
            blockers.Add(MarketplaceOrderPaymentBlockers.InsufficientBalance);

        var hashSource = JsonSerializer.Serialize(new
        {
            order.Id,
            Items = quoteItems.Select(item => new { item.OrderItemId, item.Sku, item.Quantity, item.UnitPriceCents, item.LineTotalCents }),
            ProductSubtotalCents = subtotal,
            FreightCents = freight,
            AdditionalCents = additional,
            DiscountCents = discount,
            TotalChargeCents = total
        });
        var quoteHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashSource)));

        return new MarketplaceOrderPaymentQuoteResult
        {
            OrderId = order.Id,
            CurrencyId = string.IsNullOrWhiteSpace(order.CurrencyId) ? "BRL" : order.CurrencyId,
            ProductSubtotalCents = subtotal,
            FreightCents = freight,
            AdditionalCents = additional,
            DiscountCents = discount,
            TotalChargeCents = total,
            WalletBalanceCents = walletBalance,
            WalletBalanceAfterCents = walletBalance - total,
            HasSufficientBalance = walletBalance >= total,
            QuoteHash = quoteHash,
            GeneratedAt = DateTimeOffset.UtcNow,
            PaymentBlockers = blockers.OrderBy(item => item, StringComparer.Ordinal).ToList(),
            Items = quoteItems
        };
    }

    private static void ConsumeReservations(
        IReadOnlyCollection<MarketplaceOrderItem> items,
        IReadOnlyCollection<StockReservation> reservations,
        IReadOnlyDictionary<string, ProductVariant> variants,
        ISet<string> changedSkus,
        DateTimeOffset nowUtc)
    {
        var requiredBySku = items
            .Where(item => MarketplaceMappingStates.IsMapped(item.MappingState) && !string.IsNullOrWhiteSpace(item.SabrVariantSku))
            .GroupBy(item => item.SabrVariantSku!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(item => Math.Max(0, item.Quantity)), StringComparer.Ordinal);
        var consumedBySku = new Dictionary<string, int>(StringComparer.Ordinal);
        var clearedBySku = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var reservation in reservations)
        {
            var original = Math.Max(0, reservation.Quantity);
            var already = consumedBySku.GetValueOrDefault(reservation.SabrVariantSku);
            var consume = Math.Min(original, Math.Max(0, requiredBySku.GetValueOrDefault(reservation.SabrVariantSku) - already));
            reservation.Quantity = consume;
            reservation.Status = consume > 0 ? StockReservationStatus.Consumed : StockReservationStatus.Released;
            reservation.UpdatedAt = nowUtc;
            consumedBySku[reservation.SabrVariantSku] = already + consume;
            clearedBySku[reservation.SabrVariantSku] = clearedBySku.GetValueOrDefault(reservation.SabrVariantSku) + original;
        }

        foreach (var item in items)
            item.ReservedQuantity = 0;

        foreach (var sku in consumedBySku.Keys.Union(clearedBySku.Keys, StringComparer.Ordinal))
        {
            var variant = variants[sku];
            variant.PhysicalStock = Math.Max(0, variant.PhysicalStock - consumedBySku.GetValueOrDefault(sku));
            variant.ReservedStock = Math.Max(0, variant.ReservedStock - clearedBySku.GetValueOrDefault(sku));
            variant.AvailableStock = StockAvailabilityService.ComputeAvailable(variant);
            variant.InventoryVersion = checked(variant.InventoryVersion + 1);
            variant.UpdatedAt = nowUtc;
            changedSkus.Add(sku);
        }
    }

    private async Task<(List<string> Reasons, MarketplacePaymentConfirmationRequiredResult Confirmation)> EvaluateRiskAsync(
        MarketplaceOrder order,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var cutoffValue = _options.DefaultCutoffLocalTime;
        if (_options.Features.SlaByMode && !string.IsNullOrWhiteSpace(order.LogisticType))
        {
            var logistic = order.LogisticType.Trim().ToLowerInvariant();
            var shipping = order.ShippingMode?.Trim().ToLowerInvariant();
            var rule = await _db.TenantMarketplaceSlaRules.AsNoTracking()
                .Where(item => item.TenantId == order.TenantId
                               && item.ClientId == order.ClientId
                               && item.Provider == order.Provider
                               && item.LogisticType.ToLower() == logistic
                               && (item.ShippingMode == null || item.ShippingMode.ToLower() == shipping))
                .OrderByDescending(item => item.ShippingMode != null)
                .FirstOrDefaultAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(rule?.CutoffLocalTime))
                cutoffValue = rule.CutoffLocalTime;
        }

        var confirmation = new MarketplacePaymentConfirmationRequiredResult { CutoffLocalTime = cutoffValue };
        var reasons = new List<string>();
        if (!order.ShipByDeadlineAt.HasValue)
            return (reasons, confirmation);

        var timeZone = ResolveTimeZone(_options.DefaultTimeZoneId);
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var shipByLocal = TimeZoneInfo.ConvertTime(order.ShipByDeadlineAt.Value, timeZone);
        var cutoff = TimeOnly.TryParseExact(cutoffValue, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : new TimeOnly(12, 0);
        var cutoffAt = new DateTimeOffset(shipByLocal.Year, shipByLocal.Month, shipByLocal.Day, cutoff.Hour, cutoff.Minute, 0, nowLocal.Offset);
        if (nowLocal > cutoffAt || nowUtc > order.ShipByDeadlineAt.Value)
        {
            reasons.Add("PAID_AFTER_DEADLINE");
            confirmation = new MarketplacePaymentConfirmationRequiredResult
            {
                ShipByDeadlineAt = order.ShipByDeadlineAt,
                CutoffLocalTime = cutoffValue,
                NowLocal = nowLocal.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                Message = "Pagamento apos prazo/corte. Confirmar para continuar."
            };
        }

        return (reasons, confirmation);
    }

    private static (string Field, string Code)? ResolveBlockerError(IReadOnlyCollection<string> blockers)
    {
        if (blockers.Contains(MarketplaceOrderPaymentBlockers.UnmappedItem, StringComparer.Ordinal)) return ("mapping", "ML_UNMAPPED_ITEM");
        if (blockers.Contains(MarketplaceOrderPaymentBlockers.OutOfStock, StringComparer.Ordinal)) return ("stock", "OUT_OF_STOCK_FOR_PAYMENT");
        if (blockers.Contains(MarketplaceOrderPaymentBlockers.CancellationPending, StringComparer.Ordinal)) return ("cancellation", "CANCELLATION_PENDING");
        if (blockers.Contains(MarketplaceOrderPaymentBlockers.LabelMissing, StringComparer.Ordinal)) return ("label", "LABEL_REQUIRED_BEFORE_PAYMENT");
        if (blockers.Contains(MarketplaceOrderPaymentBlockers.PricingMissing, StringComparer.Ordinal)) return ("pricing", "PAYMENT_PRICE_NOT_CONFIGURED");
        if (blockers.Contains(MarketplaceOrderPaymentBlockers.InsufficientBalance, StringComparer.Ordinal)) return ("balance", "INSUFFICIENT_WALLET_BALANCE");
        if (blockers.Contains(MarketplaceOrderPaymentBlockers.NoImportedItems, StringComparer.Ordinal)) return ("items", "ORDER_HAS_NO_ITEMS");
        return null;
    }

    private bool IsNpgsql() => _db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsContextValid(string tenantId, Guid clientId, Guid orderId) =>
        !string.IsNullOrWhiteSpace(tenantId) && clientId != Guid.Empty && orderId != Guid.Empty;

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try { return string.IsNullOrWhiteSpace(timeZoneId) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch { return TimeZoneInfo.Utc; }
    }

    private static ServiceResult<T> Failure<T>(string field, string code) =>
        ServiceResult<T>.Failure([new ValidationError(field, code)]);

    private static async Task<ServiceResult<T>> RollbackFailureAsync<T>(
        IDbContextTransaction? transaction,
        string field,
        string code,
        CancellationToken cancellationToken)
    {
        if (transaction != null)
            await transaction.RollbackAsync(cancellationToken);
        return Failure<T>(field, code);
    }
}
