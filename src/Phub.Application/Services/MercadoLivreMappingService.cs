using System.Text.Json;
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
    private readonly CatalogAuthorizationService _catalogAuthorizationService;
    private readonly MercadoLivreOAuthService _oauthService;
    private readonly IMercadoLivreApiClient _mercadoLivreApiClient;

    public MercadoLivreMappingService(
        IAppDbContext dbContext,
        CatalogAuthorizationService catalogAuthorizationService,
        MercadoLivreOAuthService oauthService,
        IMercadoLivreApiClient mercadoLivreApiClient)
    {
        _dbContext = dbContext;
        _catalogAuthorizationService = catalogAuthorizationService;
        _oauthService = oauthService;
        _mercadoLivreApiClient = mercadoLivreApiClient;
    }

    public async Task<ServiceResult<AdminMercadoLivreMappingResult>> UpsertAdminAsync(
        string tenantSlug,
        Guid clientId,
        AdminMercadoLivreMappingRequest request,
        Guid actorId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantSlug) || clientId == Guid.Empty)
            return ServiceResult<AdminMercadoLivreMappingResult>.Failure(ServiceErrorCodes.ValidationError, "context", "Tenant e cliente sao obrigatorios.");
        if (request == null)
            return ServiceResult<AdminMercadoLivreMappingResult>.Failure(ServiceErrorCodes.ValidationError, "request", "O comando de vinculo e obrigatorio.");
        if (request.IntegrationId == Guid.Empty)
            return ServiceResult<AdminMercadoLivreMappingResult>.Failure(ServiceErrorCodes.ValidationError, "integrationId", "A conexao Mercado Livre e obrigatoria.");
        if (!MercadoLivreSellerIdParser.TryParseRequired(request.SellerId, out var sellerId))
            return ServiceResult<AdminMercadoLivreMappingResult>.Failure(ServiceErrorCodes.ValidationError, "sellerId", "SellerId deve ser numerico.");
        if (string.IsNullOrWhiteSpace(request.ItemId))
            return ServiceResult<AdminMercadoLivreMappingResult>.Failure(ServiceErrorCodes.ValidationError, "itemId", "O anuncio e obrigatorio.");
        if (!Sku.TryParse(request.SabrVariantSku, out var parsedSku))
            return ServiceResult<AdminMercadoLivreMappingResult>.Failure(ServiceErrorCodes.ValidationError, "sabrVariantSku", "A SKU interna e invalida.");
        if (request.ExpectedMappingVersion < 0)
            return ServiceResult<AdminMercadoLivreMappingResult>.Failure(ServiceErrorCodes.ValidationError, "expectedMappingVersion", "A versao esperada nao pode ser negativa.");

        var normalizedTenantSlug = tenantSlug.Trim().ToLowerInvariant();
        var tenant = await _dbContext.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Slug == normalizedTenantSlug, cancellationToken);
        if (tenant == null)
            return ServiceResult<AdminMercadoLivreMappingResult>.NotFound("tenantSlug", "Tenant nao encontrado.");

        var clientExists = await _dbContext.Clients.AsNoTracking().AnyAsync(
            item => item.Id == clientId && item.TenantId == tenant.Id,
            cancellationToken);
        if (!clientExists)
            return ServiceResult<AdminMercadoLivreMappingResult>.NotFound("clientId", "Cliente nao encontrado neste tenant.");

        var connection = await _dbContext.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.Id == request.IntegrationId
                    && item.TenantId == tenant.Id
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.MercadoLivre
                    && item.SellerId == sellerId,
            cancellationToken);
        if (connection == null)
            return ServiceResult<AdminMercadoLivreMappingResult>.NotFound("integrationId", "Conexao Mercado Livre nao encontrada para este tenant, cliente e seller.");

        var variant = await _dbContext.ProductVariants.AsNoTracking().FirstOrDefaultAsync(
            item => item.VariantSku == parsedSku.Value && item.IsActive,
            cancellationToken);
        if (variant == null)
            return ServiceResult<AdminMercadoLivreMappingResult>.NotFound("sabrVariantSku", "Variante ativa nao encontrada. Complete o produto antes de vincular.");

        var product = await _dbContext.Products.AsNoTracking().FirstOrDefaultAsync(
            item => item.Sku == variant.BaseSku && item.IsActive,
            cancellationToken);
        if (product == null)
            return ServiceResult<AdminMercadoLivreMappingResult>.NotFound("sabrVariantSku", "O produto base da variante nao existe ou esta inativo.");

        if (!await _catalogAuthorizationService.IsSkuAllowedAsync(tenant.Id, clientId, product.Sku, cancellationToken))
            return ServiceResult<AdminMercadoLivreMappingResult>.Failure(
                ServiceErrorCodes.SkuNotAuthorized,
                "sabrVariantSku",
                "A SKU escolhida nao pertence ao catalogo autorizado para este cliente.");

        string accessToken;
        try
        {
            accessToken = await _oauthService.GetValidAccessTokenAsync(connection, cancellationToken);
        }
        catch (InvalidOperationException ex) when (string.Equals(ex.Message, "ML_AUTH_INVALID", StringComparison.Ordinal))
        {
            return ServiceResult<AdminMercadoLivreMappingResult>.Failure(ServiceErrorCodes.MlAuthInvalid, "integrationId", "A conexao Mercado Livre precisa ser reconectada.");
        }

        var remoteUser = await _mercadoLivreApiClient.GetUserMeAsync(accessToken, cancellationToken);
        if (!MercadoLivreSellerIdParser.TryParseRequired(remoteUser.SellerId, out var remoteSellerId) || remoteSellerId != sellerId)
            return ServiceResult<AdminMercadoLivreMappingResult>.Forbidden("sellerId", "A conexao autenticada nao pertence ao seller informado.");

        var itemId = request.ItemId.Trim().ToUpperInvariant();
        var variationId = string.IsNullOrWhiteSpace(request.VariationId) ? null : request.VariationId.Trim();
        var remoteListing = await _mercadoLivreApiClient.GetSellerItemAsync(itemId, accessToken, cancellationToken);
        if (remoteListing == null || !string.Equals(remoteListing.ItemId, itemId, StringComparison.OrdinalIgnoreCase))
            return ServiceResult<AdminMercadoLivreMappingResult>.NotFound("itemId", "Anuncio nao encontrado no Mercado Livre para esta conexao.");
        if (!MercadoLivreSellerIdParser.TryParseRequired(remoteListing.SellerId, out var listingSellerId)
            || listingSellerId != sellerId)
            return ServiceResult<AdminMercadoLivreMappingResult>.Forbidden("itemId", "O anuncio nao pertence ao seller desta conexao.");

        MercadoLivreSellerVariationDetails? remoteVariation = null;
        if (variationId != null)
        {
            remoteVariation = remoteListing.Variations.FirstOrDefault(item =>
                string.Equals(item.VariationId, variationId, StringComparison.Ordinal));
            if (remoteVariation == null)
                return ServiceResult<AdminMercadoLivreMappingResult>.NotFound("variationId", "Variacao nao encontrada neste anuncio.");
        }
        else if (remoteListing.Variations.Count > 0)
        {
            return ServiceResult<AdminMercadoLivreMappingResult>.Failure(
                ServiceErrorCodes.ValidationError,
                "variationId",
                "Selecione explicitamente a variacao do anuncio.");
        }

        var existing = await _dbContext.TenantMarketplaceListingMaps.FirstOrDefaultAsync(
            item => item.TenantId == tenant.Id
                    && item.ClientId == clientId
                    && item.Provider == MarketplaceProvider.MercadoLivre
                    && item.SellerId == sellerId
                    && item.MlItemId == itemId
                    && item.MlVariationId == variationId,
            cancellationToken);

        if (existing == null && request.ExpectedMappingVersion != 0)
            return ServiceResult<AdminMercadoLivreMappingResult>.Conflict("expectedMappingVersion", "O vinculo ainda nao existe. Recarregue o anuncio e confirme com a versao 0.");
        if (existing != null && existing.MappingVersion != request.ExpectedMappingVersion)
            return ServiceResult<AdminMercadoLivreMappingResult>.Conflict("expectedMappingVersion", $"O vinculo esta na versao {existing.MappingVersion}. Recarregue antes de confirmar.");

        var previousSku = existing?.SabrVariantSku;
        var action = existing == null ? "created" : string.Equals(previousSku, variant.VariantSku, StringComparison.Ordinal) ? "unchanged" : "remapped";
        var now = DateTimeOffset.UtcNow;

        if (existing == null)
        {
            existing = new TenantMarketplaceListingMap
            {
                TenantId = tenant.Id,
                ClientId = clientId,
                Provider = MarketplaceProvider.MercadoLivre,
                IntegrationId = connection.Id,
                SellerId = sellerId,
                MlItemId = itemId,
                MlVariationId = variationId,
                UserProductId = remoteListing.UserProductId,
                ChannelSku = NormalizeRemoteSku(remoteVariation?.SellerSku ?? remoteListing.SellerSku),
                SabrVariantSku = variant.VariantSku,
                MappingVersion = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.TenantMarketplaceListingMaps.Add(existing);
        }
        else if (action == "remapped")
        {
            existing.IntegrationId = connection.Id;
            existing.UserProductId = remoteListing.UserProductId;
            existing.ChannelSku = NormalizeRemoteSku(remoteVariation?.SellerSku ?? remoteListing.SellerSku);
            existing.SabrVariantSku = variant.VariantSku;
            existing.MappingVersion++;
            existing.UpdatedAt = now;
        }

        if (action != "unchanged")
        {
            _dbContext.AuditEvents.Add(new AuditEvent
            {
                TenantId = tenant.Id,
                ActorType = "AdminUser",
                ActorId = actorId,
                Action = action == "created" ? "MarketplaceMapping.AdminLink" : "MarketplaceMapping.AdminRemapFutureOrders",
                Entity = nameof(TenantMarketplaceListingMap),
                EntityId = existing.Id,
                RequestId = requestId == Guid.Empty ? Guid.NewGuid() : requestId,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    tenantId = tenant.Id,
                    clientId,
                    connectionId = connection.Id,
                    sellerId,
                    itemId,
                    variationId,
                    previousSabrVariantSku = previousSku,
                    sabrVariantSku = variant.VariantSku,
                    existing.MappingVersion,
                    remoteListing.Title,
                    remoteListing.Status,
                    scope = "future-orders-and-unresolved-items",
                    preservesResolvedOrderSnapshots = true,
                    createsProductVariantOrStock = false
                })
            });

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return ServiceResult<AdminMercadoLivreMappingResult>.Conflict("expectedMappingVersion", "O vinculo foi alterado por outro usuario. Recarregue antes de confirmar.");
            }
            catch (DbUpdateException)
            {
                return ServiceResult<AdminMercadoLivreMappingResult>.Conflict("listing", "O anuncio foi vinculado por outra operacao. Recarregue antes de confirmar.");
            }
        }

        return ServiceResult<AdminMercadoLivreMappingResult>.Success(new AdminMercadoLivreMappingResult
        {
            MappingId = existing.Id,
            IntegrationId = connection.Id,
            SellerId = MercadoLivreSellerIdParser.ToApiString(sellerId),
            ItemId = itemId,
            VariationId = variationId,
            PreviousSabrVariantSku = previousSku,
            SabrVariantSku = existing.SabrVariantSku,
            MappingVersion = existing.MappingVersion,
            Action = action,
            Message = action switch
            {
                "created" => "Anuncio vinculado ao SKU interno. Nenhum produto, variante ou estoque foi criado.",
                "remapped" => "Anuncio remapeado para pedidos futuros. Historico de pedidos resolvidos foi preservado.",
                _ => "O anuncio ja estava vinculado a este SKU; nenhuma alteracao foi necessaria."
            },
            UpdatedAt = existing.UpdatedAt
        });
    }

    private static string? NormalizeRemoteSku(string? value) =>
        Sku.TryParse(value, out var parsed) ? parsed.Value : null;

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
