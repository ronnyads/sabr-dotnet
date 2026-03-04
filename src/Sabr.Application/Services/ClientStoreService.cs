using Microsoft.EntityFrameworkCore;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Validation;
using Sabr.Domain.Entities;
using Sabr.Domain.Protheus;

namespace Sabr.Application.Services;

public sealed class ClientStoreService
{
    private const int StoreCodeLength = 2;
    private readonly IAppDbContext _dbContext;

    public ClientStoreService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ServiceResult<ClientStoreListResponse>> ListAsync(
        Guid clientId,
        string tenantId,
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        if (!await ClientExistsAsync(clientId, tenantId, cancellationToken))
        {
            return ServiceResult<ClientStoreListResponse>.Failure(new[]
            {
                new ValidationError("clientId", "Client not found")
            });
        }

        var query = _dbContext.ClientStores.Where(s => s.ClientId == clientId && s.TenantId == tenantId);
        if (!includeInactive)
        {
            query = query.Where(s => s.IsActive);
        }

        var items = await query
            .OrderBy(s => s.StoreCode)
            .ToListAsync(cancellationToken);

        return ServiceResult<ClientStoreListResponse>.Success(new ClientStoreListResponse
        {
            Items = items.Select(MapToResult).ToList(),
            Total = items.Count
        });
    }

    public async Task<ServiceResult<ClientStoreResult>> CreateAsync(
        Guid clientId,
        string tenantId,
        ClientStoreCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!await ClientExistsAsync(clientId, tenantId, cancellationToken))
        {
            return ServiceResult<ClientStoreResult>.Failure(new[]
            {
                new ValidationError("clientId", "Client not found")
            });
        }

        var storeCode = await GenerateNextStoreCodeAsync(clientId, tenantId, cancellationToken);

        var store = new ClientStore
        {
            ClientId = clientId,
            TenantId = tenantId,
            StoreCode = storeCode,
            Name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(),
            IsActive = true,
            ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.CREATE),
            ProtheusOperation = ProtheusOperationType.CREATE
        };

        _dbContext.ClientStores.Add(store);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<ClientStoreResult>.Success(MapToResult(store));
    }

    public async Task<ServiceResult<bool>> DeactivateAsync(
        Guid clientId,
        Guid storeId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var store = await _dbContext.ClientStores
            .FirstOrDefaultAsync(s => s.Id == storeId && s.ClientId == clientId && s.TenantId == tenantId, cancellationToken);

        if (store == null)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("storeId", "Store not found")
            });
        }

        store.IsActive = false;
        store.ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.CANCEL);
        store.ProtheusOperation = ProtheusOperationType.CANCEL;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    private async Task<bool> ClientExistsAsync(Guid clientId, string tenantId, CancellationToken cancellationToken)
    {
        return await _dbContext.Clients.AnyAsync(c => c.Id == clientId && c.TenantId == tenantId, cancellationToken);
    }

    private async Task<string> GenerateNextStoreCodeAsync(Guid clientId, string tenantId, CancellationToken cancellationToken)
    {
        var codes = await _dbContext.ClientStores
            .Where(s => s.ClientId == clientId && s.TenantId == tenantId)
            .Select(s => s.StoreCode)
            .ToListAsync(cancellationToken);

        var max = 0;
        foreach (var code in codes)
        {
            if (int.TryParse(code, out var value) && value > max)
            {
                max = value;
            }
        }

        return (max + 1).ToString($"D{StoreCodeLength}");
    }

    private static ClientStoreResult MapToResult(ClientStore store)
    {
        return new ClientStoreResult
        {
            Id = store.Id,
            ClientId = store.ClientId,
            TenantId = store.TenantId,
            StoreCode = store.StoreCode,
            Name = store.Name,
            IsActive = store.IsActive
        };
    }
}
