using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class TikTokShopMappingService
{
    private readonly IAppDbContext _dbContext;

    public TikTokShopMappingService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ServiceResult<List<TikTokShopMappingResult>>> ListMappingsAsync(
        string tenantId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var maps = await _dbContext.TenantMarketplaceListingMaps
            .Where(m => m.TenantId == tenantId && m.ClientId == clientId && m.Provider == MarketplaceProvider.TikTokShop)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new TikTokShopMappingResult
            {
                Id = m.Id,
                TikTokItemId = m.MlItemId,
                TikTokSkuId = m.MlVariationId,
                SabrVariantSku = m.SabrVariantSku,
                CreatedAt = m.CreatedAt,
                UpdatedAt = m.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        return ServiceResult<List<TikTokShopMappingResult>>.Success(maps);
    }

    public async Task<ServiceResult<TikTokShopMappingResult>> CreateMappingAsync(
        string tenantId,
        Guid clientId,
        TikTokShopCreateMappingRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.TikTokItemId))
        {
            return ServiceResult<TikTokShopMappingResult>.Failure(new[]
            {
                new ValidationError("tikTokItemId", "TikTok Item ID é obrigatório")
            });
        }

        if (string.IsNullOrWhiteSpace(request.SabrVariantSku))
        {
            return ServiceResult<TikTokShopMappingResult>.Failure(new[]
            {
                new ValidationError("sabrVariantSku", "SABR Variant SKU é obrigatório")
            });
        }

        var exists = await _dbContext.TenantMarketplaceListingMaps.AnyAsync(
            m => m.TenantId == tenantId
                 && m.ClientId == clientId
                 && m.Provider == MarketplaceProvider.TikTokShop
                 && m.MlItemId == request.TikTokItemId
                 && m.MlVariationId == request.TikTokSkuId,
            cancellationToken);

        if (exists)
        {
            return ServiceResult<TikTokShopMappingResult>.Failure(new[]
            {
                new ValidationError("mapping", "Já existe um mapeamento para este produto/variação TikTok")
            });
        }

        var mapping = new TenantMarketplaceListingMap
        {
            TenantId = tenantId,
            ClientId = clientId,
            Provider = MarketplaceProvider.TikTokShop,
            MlItemId = request.TikTokItemId,
            MlVariationId = request.TikTokSkuId,
            SabrVariantSku = request.SabrVariantSku
        };

        _dbContext.TenantMarketplaceListingMaps.Add(mapping);

        // Atualiza order items existentes que ainda não têm mapeamento
        var unmappedItems = await _dbContext.MarketplaceOrderItems
            .Where(i => i.TenantId == tenantId
                        && i.ClientId == clientId
                        && i.Provider == MarketplaceProvider.TikTokShop
                        && i.MlItemId == request.TikTokItemId
                        && i.MlVariationId == request.TikTokSkuId
                        && i.MappingState == "UNMAPPED")
            .ToListAsync(cancellationToken);

        foreach (var item in unmappedItems)
        {
            item.SabrVariantSku = request.SabrVariantSku;
            item.MappingState = "MAPPED";
            item.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<TikTokShopMappingResult>.Success(new TikTokShopMappingResult
        {
            Id = mapping.Id,
            TikTokItemId = mapping.MlItemId,
            TikTokSkuId = mapping.MlVariationId,
            SabrVariantSku = mapping.SabrVariantSku,
            CreatedAt = mapping.CreatedAt,
            UpdatedAt = mapping.UpdatedAt
        });
    }

    public async Task<ServiceResult<bool>> DeleteMappingAsync(
        string tenantId,
        Guid clientId,
        Guid mappingId,
        CancellationToken cancellationToken = default)
    {
        var mapping = await _dbContext.TenantMarketplaceListingMaps.FirstOrDefaultAsync(
            m => m.Id == mappingId
                 && m.TenantId == tenantId
                 && m.ClientId == clientId
                 && m.Provider == MarketplaceProvider.TikTokShop,
            cancellationToken);

        if (mapping == null)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("mapping", "Mapeamento não encontrado")
            });
        }

        _dbContext.TenantMarketplaceListingMaps.Remove(mapping);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<bool>.Success(true);
    }
}

public sealed class TikTokShopMappingResult
{
    public Guid Id { get; set; }
    public string TikTokItemId { get; set; } = string.Empty;
    public string? TikTokSkuId { get; set; }
    public string SabrVariantSku { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TikTokShopCreateMappingRequest
{
    public string TikTokItemId { get; set; } = string.Empty;
    public string? TikTokSkuId { get; set; }
    public string SabrVariantSku { get; set; } = string.Empty;
}
