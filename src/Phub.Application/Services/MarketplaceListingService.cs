using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class MarketplaceListingService
{
    private readonly IAppDbContext _db;
    private readonly IMercadoLivreApiClient _api;
    private readonly MercadoLivreOAuthService _oauth;
    private readonly IReadOnlyList<IMarketplaceListingAdapter> _adapters;

    public MarketplaceListingService(
        IAppDbContext db,
        IMercadoLivreApiClient api,
        MercadoLivreOAuthService oauth,
        IEnumerable<IMarketplaceListingAdapter> adapters)
    {
        _db = db;
        _api = api;
        _oauth = oauth;
        _adapters = adapters.ToList();
    }

    public async Task<ServiceResult<MarketplaceListingWorkspace>> GetAsync(
        string tenantId,
        Guid clientId,
        Guid mappingId,
        CancellationToken cancellationToken = default)
    {
        var context = await LoadAsync(tenantId, clientId, mappingId, cancellationToken);
        if (!context.Succeeded || context.Data == null)
        {
            return ServiceResult<MarketplaceListingWorkspace>.Failure(context.ErrorCode ?? ServiceErrorCodes.ValidationError, context.Errors);
        }

        return ServiceResult<MarketplaceListingWorkspace>.Success(BuildWorkspace(context.Data.Mapping, context.Data.Details));
    }

    public async Task<ServiceResult<MarketplaceListingWorkspace>> ApplyAsync(
        string tenantId,
        Guid clientId,
        Guid actorId,
        Guid mappingId,
        MarketplaceListingChangeSet changeSet,
        CancellationToken cancellationToken = default)
    {
        var context = await LoadAsync(tenantId, clientId, mappingId, cancellationToken);
        if (!context.Succeeded || context.Data == null)
        {
            return ServiceResult<MarketplaceListingWorkspace>.Failure(context.ErrorCode ?? ServiceErrorCodes.ValidationError, context.Errors);
        }

        var current = BuildWorkspace(context.Data.Mapping, context.Data.Details);
        if (changeSet.MappingVersion != context.Data.Mapping.MappingVersion
            || string.IsNullOrWhiteSpace(changeSet.EvaluationHash)
            || !string.Equals(changeSet.EvaluationHash, current.Capabilities.EvaluationHash, StringComparison.Ordinal))
        {
            return ServiceResult<MarketplaceListingWorkspace>.Conflict(
                "evaluationHash",
                "O anuncio mudou ou a avaliacao expirou. Recarregue as capacidades antes de confirmar.");
        }

        var validation = ValidateChanges(changeSet, current.Capabilities);
        if (validation.Count > 0)
        {
            return ServiceResult<MarketplaceListingWorkspace>.Failure(ServiceErrorCodes.ValidationError, validation);
        }

        if (changeSet.Title is null && changeSet.Price is null && changeSet.Description is null)
        {
            return ServiceResult<MarketplaceListingWorkspace>.Failure(
                ServiceErrorCodes.ValidationError,
                "changes",
                "Informe ao menos uma alteracao.");
        }

        await _api.UpdateListingAsync(
            context.Data.Mapping.MlItemId,
            new MercadoLivreListingUpdateRequest
            {
                Title = changeSet.Title,
                Price = changeSet.Price,
                Description = changeSet.Description
            },
            context.Data.AccessToken,
            cancellationToken);

        _db.AuditEvents.Add(new AuditEvent
        {
            TenantId = tenantId,
            ActorType = "ClientUser",
            ActorId = actorId == Guid.Empty ? null : actorId,
            Action = "MarketplaceListing.SynchronizeChanges",
            Entity = nameof(TenantMarketplaceListingMap),
            EntityId = mappingId,
            RequestId = Guid.NewGuid(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                context.Data.Mapping.Provider,
                context.Data.Mapping.SellerId,
                context.Data.Mapping.MlItemId,
                context.Data.Mapping.MlVariationId,
                context.Data.Mapping.UserProductId,
                context.Data.Mapping.MappingVersion,
                changedFields = new[]
                {
                    changeSet.Title is null ? null : "title",
                    changeSet.Price is null ? null : "price",
                    changeSet.Description is null ? null : "description"
                }.Where(item => item != null)
            })
        });
        await _db.SaveChangesAsync(cancellationToken);

        var refreshed = await _api.GetSellerItemAsync(context.Data.Mapping.MlItemId, context.Data.AccessToken, cancellationToken);
        return ServiceResult<MarketplaceListingWorkspace>.Success(
            BuildWorkspace(context.Data.Mapping, refreshed ?? context.Data.Details));
    }

    private async Task<ServiceResult<ListingContext>> LoadAsync(
        string tenantId,
        Guid clientId,
        Guid mappingId,
        CancellationToken cancellationToken)
    {
        var mapping = await _db.TenantMarketplaceListingMaps
            .FirstOrDefaultAsync(item => item.Id == mappingId && item.TenantId == tenantId && item.ClientId == clientId, cancellationToken);
        if (mapping == null) return ServiceResult<ListingContext>.NotFound("mappingId", "Vinculo nao encontrado.");
        if (mapping.Provider != MarketplaceProvider.MercadoLivre)
        {
            return ServiceResult<ListingContext>.Failure(ServiceErrorCodes.ValidationError, "provider", "Este canal ainda nao suporta edicao de anuncios.");
        }

        var connection = await _db.TenantMarketplaceConnections.FirstOrDefaultAsync(
            item => item.TenantId == tenantId
                    && item.ClientId == clientId
                    && item.Provider == mapping.Provider
                    && item.SellerId == mapping.SellerId
                    && (!mapping.IntegrationId.HasValue || item.Id == mapping.IntegrationId.Value),
            cancellationToken);
        if (connection == null) return ServiceResult<ListingContext>.NotFound("integration", "Integracao autorizada nao encontrada.");

        var token = await _oauth.GetValidAccessTokenAsync(connection, cancellationToken);
        var details = await _api.GetSellerItemAsync(mapping.MlItemId, token, cancellationToken);
        if (details == null) return ServiceResult<ListingContext>.NotFound("itemId", "Anuncio nao encontrado no Mercado Livre.");
        mapping.UserProductId ??= details.UserProductId;
        return ServiceResult<ListingContext>.Success(new ListingContext(mapping, details, token));
    }

    private MarketplaceListingWorkspace BuildWorkspace(TenantMarketplaceListingMap mapping, MercadoLivreSellerItemDetails details)
    {
        var adapter = _adapters.FirstOrDefault(item => item.CanHandle(details))
            ?? throw new InvalidOperationException("MARKETPLACE_LISTING_ADAPTER_NOT_FOUND");
        var listing = adapter.Normalize(mapping, details);
        return new MarketplaceListingWorkspace
        {
            Listing = listing,
            Capabilities = adapter.Evaluate(listing, details, DateTimeOffset.UtcNow)
        };
    }

    private static List<ValidationError> ValidateChanges(
        MarketplaceListingChangeSet changes,
        MarketplaceListingCapabilities capabilities)
    {
        var errors = new List<ValidationError>();
        Check("title", changes.Title is not null);
        Check("price", changes.Price.HasValue);
        Check("description", changes.Description is not null);
        if (changes.Title is not null && string.IsNullOrWhiteSpace(changes.Title)) errors.Add(new ValidationError("title", "O titulo nao pode ficar vazio."));
        if (changes.Price.HasValue && changes.Price.Value <= 0) errors.Add(new ValidationError("price", "O preco deve ser maior que zero."));
        return errors;

        void Check(string field, bool changed)
        {
            if (changed && (!capabilities.Fields.TryGetValue(field, out var capability) || !capability.Editable))
            {
                errors.Add(new ValidationError(field, capability?.Reason ?? "Campo nao autorizado pelo Capability Engine."));
            }
        }
    }

    private sealed record ListingContext(
        TenantMarketplaceListingMap Mapping,
        MercadoLivreSellerItemDetails Details,
        string AccessToken);
}
