namespace Phub.Application.Abstractions;

/// <summary>
/// Serviço de cache distribuído (Redis em produção, MemoryCache em dev/testes).
/// Todos os métodos são fire-safe: nunca lançam exceção — falha silenciosa, aplica fallback ao banco.
/// </summary>
public interface ICacheService
{
    /// <summary>Retorna o item do cache, ou null se expirado/ausente/erro.</summary>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class;

    /// <summary>Armazena o item no cache com TTL absoluto.</summary>
    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class;

    /// <summary>Remove uma chave do cache.</summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Remove todas as chaves que começam com o prefixo informado.</summary>
    Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cache-aside pattern: retorna o valor do cache se existir,
    /// senão executa <paramref name="factory"/> e armazena o resultado.
    /// </summary>
    Task<T> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan ttl,
        CancellationToken cancellationToken = default) where T : class;
}
