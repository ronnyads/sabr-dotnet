using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Domain.ValueObjects;

namespace Phub.Application.Services;

public sealed class MercadoLivreMappingService
{
    private readonly IAppDbContext _dbContext;

    public MercadoLivreMappingService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ServiceResult<List<MercadoLivreListingMapResult>>> ListAsync(
        string tenantId,
        Guid clientId,
        string? sellerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty)
        {
            return ServiceResult<List<MercadoLivreListingMapResult>>.Failure(new[]
            {
                new ValidationError("context", "Invalid tenant/client context")
            });
        }

        var query = _dbContext.TenantMarketplaceListingMaps
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId
                           && item.ClientId == clientId
                           && item.Provider == MarketplaceProvider.MercadoLivre);

        if (!MercadoLivreSellerIdParser.TryParseOptional(sellerId, out var normalizedSeller))
        {
            return ServiceResult<List<MercadoLivreListingMapResult>>.Failure(new[]
            {
                new ValidationError("sellerId", "SellerId must be numeric")
            });
        }

        if (normalizedSeller.HasValue)
        {
            query = query.Where(item => item.SellerId == normalizedSeller.Value);
        }

        var items = await query
            .OrderBy(item => item.SellerId)
            .ThenBy(item => item.MlItemId)
            .ThenBy(item => item.MlVariationId)
            .Select(item => new MercadoLivreListingMapResult
            {
                Id = item.Id,
                IntegrationId = item.IntegrationId,
                SellerId = MercadoLivreSellerIdParser.ToApiString(item.SellerId),
                MlItemId = item.MlItemId,
                MlVariationId = item.MlVariationId,
                SabrVariantSku = item.SabrVariantSku,
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        return ServiceResult<List<MercadoLivreListingMapResult>>.Success(items);
    }

    public async Task<ServiceResult<MercadoLivreListingMapResult>> CreateAsync(
        string tenantId,
        Guid clientId,
        MercadoLivreCreateMappingRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateRequest(tenantId, clientId, request);
        if (errors.Count > 0)
        {
            return ServiceResult<MercadoLivreListingMapResult>.Failure(errors);
        }

        if (!MercadoLivreSellerIdParser.TryParseRequired(request.SellerId, out var sellerId))
        {
            return ServiceResult<MercadoLivreListingMapResult>.Failure(new[]
            {
                new ValidationError("sellerId", "SellerId must be numeric")
            });
        }

        var mlItemId = request.MlItemId.Trim();
        var mlVariationId = string.IsNullOrWhiteSpace(request.MlVariationId)
            ? null
            : request.MlVariationId.Trim();
        var sku = Sku.Normalize(request.SabrVariantSku);

        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.MercadoLivre
                    && item.SellerId == sellerId,
            cancellationToken);
        if (connection == null)
        {
            return ServiceResult<MercadoLivreListingMapResult>.Failure(new[]
            {
                new ValidationError("sellerId", "Seller is not connected")
            });
        }

        var variantExists = await _dbContext.ProductVariants.AnyAsync(
            item => item.VariantSku == sku,
            cancellationToken);
        if (!variantExists)
        {
            return ServiceResult<MercadoLivreListingMapResult>.Failure(new[]
            {
                new ValidationError("sabrVariantSku", "Variant SKU not found")
            });
        }

        var existing = await _dbContext.TenantMarketplaceListingMaps.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.MercadoLivre
                    && item.SellerId == sellerId
                    && item.IntegrationId == connection.Id
                    && item.MlItemId == mlItemId
                    && item.MlVariationId == mlVariationId,
            cancellationToken);

        if (existing == null)
        {
            existing = new TenantMarketplaceListingMap
            {
                TenantId = tenantId,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                IntegrationId = connection.Id,
                SellerId = sellerId,
                MlItemId = mlItemId,
                MlVariationId = mlVariationId,
                SabrVariantSku = sku
            };
            _dbContext.TenantMarketplaceListingMaps.Add(existing);
        }
        else
        {
            existing.SabrVariantSku = sku;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<MercadoLivreListingMapResult>.Success(new MercadoLivreListingMapResult
        {
            Id = existing.Id,
            IntegrationId = existing.IntegrationId,
            SellerId = MercadoLivreSellerIdParser.ToApiString(existing.SellerId),
            MlItemId = existing.MlItemId,
            MlVariationId = existing.MlVariationId,
            SabrVariantSku = existing.SabrVariantSku,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = existing.UpdatedAt
        });
    }

    public async Task<ServiceResult<bool>> DeleteAsync(
        string tenantId,
        Guid clientId,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || clientId == Guid.Empty || id == Guid.Empty)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("id", "Invalid mapping id")
            });
        }

        var entity = await _dbContext.TenantMarketplaceListingMaps.FirstOrDefaultAsync(
            item => item.Id == id
                    && item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.MercadoLivre,
            cancellationToken);

        if (entity == null)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("id", "Mapping not found")
            });
        }

        _dbContext.TenantMarketplaceListingMaps.Remove(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    private static List<ValidationError> ValidateRequest(
        string tenantId,
        Guid clientId,
        MercadoLivreCreateMappingRequest request)
    {
        var errors = new List<ValidationError>();
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            errors.Add(new ValidationError("tenantId", "TenantId is required"));
        }

        if (clientId == Guid.Empty)
        {
            errors.Add(new ValidationError("clientId", "ClientId is required"));
        }

        if (request == null)
        {
            errors.Add(new ValidationError("request", "Request is required"));
            return errors;
        }

        if (string.IsNullOrWhiteSpace(request.SellerId))
        {
            errors.Add(new ValidationError("sellerId", "SellerId is required"));
        }

        if (string.IsNullOrWhiteSpace(request.MlItemId))
        {
            errors.Add(new ValidationError("mlItemId", "MlItemId is required"));
        }

        if (string.IsNullOrWhiteSpace(request.SabrVariantSku))
        {
            errors.Add(new ValidationError("sabrVariantSku", "SabrVariantSku is required"));
        }

        return errors;
    }
}
