using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Sabr.Application.Abstractions;

namespace Sabr.Infrastructure.Services;

/// <summary>
/// Implementação de <see cref="ICacheService"/> usando <see cref="IDistributedCache"/>.
/// Em produção injeta Redis; em dev/testes injeta MemoryDistributedCache.
/// Todos os métodos são fire-safe: jamais propagam exceção — erro vira log de warning.
/// </summary>
public sealed class DistributedCacheService : ICacheService
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<DistributedCacheService> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public DistributedCacheService(IDistributedCache cache, ILogger<DistributedCacheService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        try
        {
            var bytes = await _cache.GetAsync(key, cancellationToken);
            if (bytes is null or { Length: 0 })
                return null;

            return JsonSerializer.Deserialize<T>(bytes, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache GET failed for key '{Key}' — falling back to source", key);
            return null;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _jsonOptions);
            var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl };
            await _cache.SetAsync(key, bytes, options, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache SET failed for key '{Key}' — continuing without cache", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await _cache.RemoveAsync(key, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache REMOVE failed for key '{Key}'", key);
        }
    }

    public async Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        // IDistributedCache não suporta scan nativo. 
        // Com Redis puro, usaríamos IServer.KeysAsync. 
        // Esta implementação é intencional: no fluxo de invalidação por prefixo,
        // chamamos RemoveAsync para cada chave conhecida individualmente via CacheKeys.
        // Para invalidação ampla (ex: flush de categorias), use tags ou TTL curto.
        _logger.LogDebug("RemoveByPrefix '{Prefix}' called — prefix scan not supported on IDistributedCache; use explicit key removal", prefix);
        await Task.CompletedTask;
    }

    public async Task<T> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan ttl,
        CancellationToken cancellationToken = default) where T : class
    {
        var cached = await GetAsync<T>(key, cancellationToken);
        if (cached is not null)
            return cached;

        var value = await factory(cancellationToken);
        await SetAsync(key, value, ttl, cancellationToken);
        return value;
    }
}
