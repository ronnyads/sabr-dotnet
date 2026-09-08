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
    public const string ListingChangeOperation = "LISTING_CHANGE";
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

    public async Task<ServiceResult<MarketplaceListingChangeDraft>> SaveDraftAsync(
        string tenantId,
        Guid clientId,
        Guid actorId,
        Guid mappingId,
        MarketplaceListingChangeSet changeSet,
        CancellationToken cancellationToken = default)
    {
        var context = await LoadAsync(tenantId, clientId, mappingId, cancellationToken);
        if (!context.Succeeded || context.Data == null)
            return ServiceResult<MarketplaceListingChangeDraft>.Failure(context.ErrorCode ?? ServiceErrorCodes.ValidationError, context.Errors);

        var current = BuildWorkspace(context.Data.Mapping, context.Data.Details);
        if (changeSet.MappingVersion != context.Data.Mapping.MappingVersion
            || string.IsNullOrWhiteSpace(changeSet.EvaluationHash)
            || !string.Equals(changeSet.EvaluationHash, current.Capabilities.EvaluationHash, StringComparison.Ordinal))
        {
            return ServiceResult<MarketplaceListingChangeDraft>.Conflict(
                "evaluationHash",
                "O anuncio mudou ou a avaliacao expirou. Recarregue as capacidades antes de salvar o rascunho.");
        }

        var validation = ValidateChanges(changeSet, current.Capabilities);
        if (validation.Count > 0)
            return ServiceResult<MarketplaceListingChangeDraft>.Failure(ServiceErrorCodes.ValidationError, validation);
        if (changeSet.Title is null && changeSet.Price is null && changeSet.Description is null)
            return ServiceResult<MarketplaceListingChangeDraft>.Failure(ServiceErrorCodes.ValidationError, "changes", "Informe ao menos uma alteracao.");

        var now = DateTimeOffset.UtcNow;
        var envelope = new ListingChangeDraftPayload(mappingId, actorId, changeSet);
        var job = new MarketplaceOperationJob
        {
            TenantId = tenantId,
            ClientId = clientId,
            Provider = MarketplaceProvider.MercadoLivre,
            OperationType = ListingChangeOperation,
            Status = "DRAFT",
            PayloadJson = JsonSerializer.Serialize(envelope),
            Total = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.MarketplaceOperationJobs.Add(job);
        _db.AuditEvents.Add(new AuditEvent
        {
            TenantId = tenantId,
            ActorType = "ClientUser",
            ActorId = actorId == Guid.Empty ? null : actorId,
            Action = "MarketplaceListing.SaveChangeDraft",
            Entity = nameof(MarketplaceOperationJob),
            EntityId = job.Id,
            RequestId = Guid.NewGuid(),
            MetadataJson = JsonSerializer.Serialize(new { mappingId, context.Data.Mapping.MlItemId, context.Data.Mapping.MappingVersion })
        });
        await _db.SaveChangesAsync(cancellationToken);
        return ServiceResult<MarketplaceListingChangeDraft>.Success(new MarketplaceListingChangeDraft
        {
            DraftId = job.Id,
            MappingId = mappingId,
            Status = job.Status,
            Changes = changeSet,
            CreatedAt = job.CreatedAt
        });
    }

    public async Task<ServiceResult<MarketplaceListingWorkspace>> ApplyDraftAsync(
        string tenantId,
        Guid clientId,
        Guid actorId,
        Guid mappingId,
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        var job = await _db.MarketplaceOperationJobs.AsNoTracking().FirstOrDefaultAsync(item =>
            item.Id == draftId
            && item.TenantId == tenantId
            && item.ClientId == clientId
            && item.Provider == MarketplaceProvider.MercadoLivre
            && item.OperationType == ListingChangeOperation,
            cancellationToken);
        if (job == null) return ServiceResult<MarketplaceListingWorkspace>.NotFound("draftId", "Rascunho nao encontrado.");
        if (!string.Equals(job.Status, "DRAFT", StringComparison.Ordinal))
            return ServiceResult<MarketplaceListingWorkspace>.Conflict("draftId", "Este rascunho ja foi processado ou substituido.");

        var payload = JsonSerializer.Deserialize<ListingChangeDraftPayload>(job.PayloadJson);
        if (payload == null || payload.MappingId != mappingId)
            return ServiceResult<MarketplaceListingWorkspace>.Failure(ServiceErrorCodes.ValidationError, "draftId", "Rascunho invalido para este anuncio.");

        var startedAt = DateTimeOffset.UtcNow;
        var claimed = await _db.MarketplaceOperationJobs
            .Where(item => item.Id == draftId && item.Status == "DRAFT")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, "PROCESSING")
                .SetProperty(item => item.StartedAt, startedAt)
                .SetProperty(item => item.Attempts, item => item.Attempts + 1)
                .SetProperty(item => item.UpdatedAt, startedAt), cancellationToken);
        if (claimed == 0)
            return ServiceResult<MarketplaceListingWorkspace>.Conflict("draftId", "Este rascunho foi confirmado por outra sessao.");

        ServiceResult<MarketplaceListingWorkspace> result;
        try
        {
            result = await ApplyAsync(tenantId, clientId, actorId, mappingId, payload.Changes, cancellationToken);
        }
        catch (Exception ex)
        {
            var failedAt = DateTimeOffset.UtcNow;
            var error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            await _db.MarketplaceOperationJobs.Where(item => item.Id == draftId).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, "FAILED")
                .SetProperty(item => item.Processed, 1)
                .SetProperty(item => item.Failed, 1)
                .SetProperty(item => item.LastError, error)
                .SetProperty(item => item.CompletedAt, failedAt)
                .SetProperty(item => item.UpdatedAt, failedAt), CancellationToken.None);
            throw;
        }
        var status = result.Succeeded ? "COMPLETED" : result.ErrorCode == ServiceErrorCodes.ConcurrencyConflict ? "SUPERSEDED" : "FAILED";
        var completedAt = DateTimeOffset.UtcNow;
        var lastError = result.Succeeded ? null : string.Join("; ", result.Errors.Select(error => error.Message));
        await _db.MarketplaceOperationJobs.Where(item => item.Id == draftId).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Status, status)
            .SetProperty(item => item.Processed, 1)
            .SetProperty(item => item.Succeeded, result.Succeeded ? 1 : 0)
            .SetProperty(item => item.Failed, result.Succeeded ? 0 : 1)
            .SetProperty(item => item.LastError, lastError)
            .SetProperty(item => item.CompletedAt, completedAt)
            .SetProperty(item => item.UpdatedAt, completedAt), cancellationToken);
        return result;
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

    private sealed record ListingChangeDraftPayload(
        Guid MappingId,
        Guid ActorId,
        MarketplaceListingChangeSet Changes);
}
