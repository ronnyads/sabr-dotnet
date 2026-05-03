using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
using Phub.Domain.Entities;

namespace Phub.Application.Services;

/// <summary>
/// Serviço para gerenciar configurações de prompts de IA.
/// </summary>
public sealed class AiPromptConfigService
{
    private readonly IAppDbContext _dbContext;

    public AiPromptConfigService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Lista todos os prompts configurados.
    /// </summary>
    public async Task<List<AiPromptConfigResult>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.AiPromptConfigs
            .AsNoTracking()
            .OrderBy(x => x.Feature)
            .ThenBy(x => x.Channel)
            .Select(x => new AiPromptConfigResult
            {
                Id = x.Id,
                Feature = x.Feature,
                Channel = x.Channel,
                Name = x.Name,
                Prompt = x.Prompt,
                IsActive = x.IsActive,
                UpdatedAt = x.UpdatedAt
            })
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Obtém um prompt específico.
    /// </summary>
    public async Task<AiPromptConfigResult?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.AiPromptConfigs
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new AiPromptConfigResult
            {
                Id = x.Id,
                Feature = x.Feature,
                Channel = x.Channel,
                Name = x.Name,
                Prompt = x.Prompt,
                IsActive = x.IsActive,
                UpdatedAt = x.UpdatedAt
            })
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Obtém o prompt ativo para uma feature e canal específicos.
    /// </summary>
    public async Task<AiPromptConfigResult?> GetActiveByFeatureAndChannelAsync(
        string feature,
        string channel,
        CancellationToken cancellationToken = default)
    {
        // Procura primeiro por canal específico, depois por "*" (todos)
        return await _dbContext.AiPromptConfigs
            .AsNoTracking()
            .Where(x => x.Feature == feature && x.IsActive && (x.Channel == channel || x.Channel == "*"))
            .OrderByDescending(x => x.Channel == channel)  // Preferir canal específico
            .Select(x => new AiPromptConfigResult
            {
                Id = x.Id,
                Feature = x.Feature,
                Channel = x.Channel,
                Name = x.Name,
                Prompt = x.Prompt,
                IsActive = x.IsActive,
                UpdatedAt = x.UpdatedAt
            })
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Cria ou atualiza um prompt.
    /// </summary>
    public async Task<ServiceResult<AiPromptConfigResult>> UpsertAsync(
        AiPromptConfigUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Feature))
            return ServiceResult<AiPromptConfigResult>.Failure(new[] { new ValidationError("feature", "Feature é obrigatória") });

        if (string.IsNullOrWhiteSpace(request.Channel))
            return ServiceResult<AiPromptConfigResult>.Failure(new[] { new ValidationError("channel", "Channel é obrigatório") });

        if (string.IsNullOrWhiteSpace(request.Name))
            return ServiceResult<AiPromptConfigResult>.Failure(new[] { new ValidationError("name", "Name é obrigatório") });

        if (string.IsNullOrWhiteSpace(request.Prompt))
            return ServiceResult<AiPromptConfigResult>.Failure(new[] { new ValidationError("prompt", "Prompt é obrigatório") });

        AiPromptConfig config;

        if (request.Id.HasValue && request.Id.Value != Guid.Empty)
        {
            // Update
            config = await _dbContext.AiPromptConfigs.FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken);
            if (config == null)
                return ServiceResult<AiPromptConfigResult>.Failure(new[] { new ValidationError("id", "Prompt não encontrado") });

            config.Feature = request.Feature.Trim();
            config.Channel = request.Channel.Trim();
            config.Name = request.Name.Trim();
            config.Prompt = request.Prompt.Trim();
            config.IsActive = request.IsActive;
            config.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            // Create
            config = new AiPromptConfig
            {
                Id = Guid.NewGuid(),
                Feature = request.Feature.Trim(),
                Channel = request.Channel.Trim(),
                Name = request.Name.Trim(),
                Prompt = request.Prompt.Trim(),
                IsActive = request.IsActive,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            _dbContext.AiPromptConfigs.Add(config);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<AiPromptConfigResult>.Success(new AiPromptConfigResult
        {
            Id = config.Id,
            Feature = config.Feature,
            Channel = config.Channel,
            Name = config.Name,
            Prompt = config.Prompt,
            IsActive = config.IsActive,
            UpdatedAt = config.UpdatedAt
        });
    }

    /// <summary>
    /// Deleta um prompt.
    /// </summary>
    public async Task<ServiceResult<bool>> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var config = await _dbContext.AiPromptConfigs.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (config == null)
            return ServiceResult<bool>.Failure(new[] { new ValidationError("id", "Prompt não encontrado") });

        _dbContext.AiPromptConfigs.Remove(config);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<bool>.Success(true);
    }
}
