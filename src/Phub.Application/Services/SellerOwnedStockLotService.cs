using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Domain.Entities;

namespace Phub.Application.Services;

public sealed class SellerOwnedStockLotService
{
    private readonly IAppDbContext _db;
    public SellerOwnedStockLotService(IAppDbContext db) => _db = db;

    public Task<SellerOwnedStockLot> CreateFromSettledPurchaseAsync(
        string tenantId, Guid clientId, long sellerId, string variantSku, int quantity,
        long unitCostCents, string purchaseSettlementId, DateTimeOffset acquiredAt,
        Guid actorUserId, CancellationToken cancellationToken = default) =>
        CreateAsync(tenantId, clientId, sellerId, variantSku, quantity, unitCostCents,
            SellerOwnedStockLotSources.SettledPurchase, purchaseSettlementId, null,
            acquiredAt, actorUserId, "Compra liquidada", false, cancellationToken);

    public Task<SellerOwnedStockLot> CreateExceptionalCorrectionAsync(
        string tenantId, Guid clientId, long sellerId, string variantSku, int quantity,
        long unitCostCents, string correctionId, string evidenceReference, string reason,
        DateTimeOffset acquiredAt, Guid superAdminUserId, CancellationToken cancellationToken = default) =>
        CreateAsync(tenantId, clientId, sellerId, variantSku, quantity, unitCostCents,
            SellerOwnedStockLotSources.SuperAdminCorrection, correctionId, evidenceReference,
            acquiredAt, superAdminUserId, reason, true, cancellationToken);

    private async Task<SellerOwnedStockLot> CreateAsync(
        string tenantId, Guid clientId, long sellerId, string variantSku, int quantity,
        long unitCostCents, string sourceType, string sourceId, string? evidenceReference,
        DateTimeOffset acquiredAt, Guid actorUserId, string reason, bool exceptional,
        CancellationToken cancellationToken)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (unitCostCents <= 0) throw new ArgumentOutOfRangeException(nameof(unitCostCents));
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Fonte obrigatória.", nameof(sourceId));
        if (exceptional && (string.IsNullOrWhiteSpace(evidenceReference) || string.IsNullOrWhiteSpace(reason)))
            throw new InvalidOperationException("Correção excepcional exige evidência e motivo.");

        var normalizedSku = Phub.Domain.ValueObjects.Sku.Normalize(variantSku);
        var existing = await _db.SellerOwnedStockLots.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.ClientId == clientId && x.SellerId == sellerId
            && x.SourceType == sourceType && x.SourceId == sourceId, cancellationToken);
        if (existing != null) return existing;

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            ProductVariant? variant;
            if (_db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
                variant = await _db.ProductVariants.FromSqlInterpolated(
                    $"SELECT * FROM product_variants WHERE variant_sku = {normalizedSku} FOR UPDATE")
                    .SingleOrDefaultAsync(cancellationToken);
            else
                variant = await _db.ProductVariants.SingleOrDefaultAsync(x => x.VariantSku == normalizedSku, cancellationToken);

            if (variant == null) throw new InvalidOperationException("SKU interno não encontrado.");
            if (StockAvailabilityService.ComputeAvailable(variant) < quantity)
                throw new InvalidOperationException("Estoque geral disponível insuficiente para transferir ao seller.");

            var now = DateTimeOffset.UtcNow;
            var lot = new SellerOwnedStockLot
            {
                TenantId = tenantId, ClientId = clientId, SellerId = sellerId, VariantSku = normalizedSku,
                OriginalQuantity = quantity, AvailableQuantity = quantity, UnitCostCents = unitCostCents,
                SourceType = sourceType, SourceId = sourceId.Trim(), EvidenceReference = evidenceReference?.Trim(),
                AcquiredAt = acquiredAt, CreatedAt = now, CreatedByUserId = actorUserId, Reason = reason.Trim()
            };
            _db.SellerOwnedStockLots.Add(lot);
            variant.ClientOwnedStock = checked(variant.ClientOwnedStock + quantity);
            variant.AvailableStock = StockAvailabilityService.ComputeAvailable(variant);
            variant.InventoryVersion = checked(variant.InventoryVersion + 1);
            variant.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            return lot;
        }
        catch
        {
            if (transaction != null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
