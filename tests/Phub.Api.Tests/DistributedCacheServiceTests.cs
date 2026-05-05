using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Phub.Infrastructure.Services;

namespace Phub.Api.Tests;

/// <summary>
/// Testes unitários para DistributedCacheService (usando MemoryDistributedCache).
/// Testa serialização, TTL, fire-safe (nunca lança exceção), e o padrão cache-aside.
/// </summary>
public sealed class DistributedCacheServiceTests
{
    private static DistributedCacheService CreateCache(IDistributedCache? inner = null) =>
        new(inner ?? new MemoryDistributedCache(
                Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())),
            NullLogger<DistributedCacheService>.Instance);

    // ── GetAsync / SetAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task SetAndGet_RoundTrip_ReturnsOriginalValue()
    {
        var cache = CreateCache();
        var payload = new SampleDto("hello", 42);

        await cache.SetAsync("key1", payload, TimeSpan.FromMinutes(5));
        var result = await cache.GetAsync<SampleDto>("key1");

        Assert.NotNull(result);
        Assert.Equal("hello", result!.Name);
        Assert.Equal(42, result.Count);
    }

    [Fact]
    public async Task GetAsync_MissingKey_ReturnsNull()
    {
        var cache = CreateCache();
        var result = await cache.GetAsync<SampleDto>("nonexistent-key");
        Assert.Null(result);
    }

    [Fact]
    public async Task SetAsync_WithExpiredTtl_ReturnsNull()
    {
        var cache = CreateCache();
        await cache.SetAsync("expiring", new SampleDto("x", 1), TimeSpan.FromMilliseconds(1));
        await Task.Delay(20); // espera expirar

        var result = await cache.GetAsync<SampleDto>("expiring");
        Assert.Null(result);
    }

    // ── RemoveAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveAsync_ExistingKey_BecomesNull()
    {
        var cache = CreateCache();
        await cache.SetAsync("rm-key", new SampleDto("remove-me", 0), TimeSpan.FromMinutes(5));

        await cache.RemoveAsync("rm-key");
        var result = await cache.GetAsync<SampleDto>("rm-key");
        Assert.Null(result);
    }

    [Fact]
    public async Task RemoveAsync_NonExistentKey_DoesNotThrow()
    {
        var cache = CreateCache();
        // Não deve lançar exceção
        await cache.RemoveAsync("ghost-key");
    }

    // ── GetOrSetAsync (cache-aside) ───────────────────────────────────────────

    [Fact]
    public async Task GetOrSetAsync_CacheMiss_CallsFactory()
    {
        var cache = CreateCache();
        var factoryCalls = 0;

        var result = await cache.GetOrSetAsync(
            "aside-key",
            async ct =>
            {
                factoryCalls++;
                await Task.Yield();
                return new SampleDto("from-factory", 99);
            },
            TimeSpan.FromMinutes(5));

        Assert.NotNull(result);
        Assert.Equal("from-factory", result.Name);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public async Task GetOrSetAsync_CacheHit_DoesNotCallFactory()
    {
        var cache = CreateCache();
        var factoryCalls = 0;

        // Popula primeiro
        await cache.SetAsync("hit-key", new SampleDto("cached", 1), TimeSpan.FromMinutes(5));

        var result = await cache.GetOrSetAsync(
            "hit-key",
            async ct =>
            {
                factoryCalls++;
                await Task.Yield();
                return new SampleDto("should-not-be-called", 999);
            },
            TimeSpan.FromMinutes(5));

        Assert.Equal("cached", result.Name);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task GetOrSetAsync_StoredResultIsServedOnSubsequentCalls()
    {
        var cache = CreateCache();
        var factoryCalls = 0;

        for (var i = 0; i < 5; i++)
        {
            await cache.GetOrSetAsync(
                "same-key",
                async ct =>
                {
                    factoryCalls++;
                    await Task.Yield();
                    return new SampleDto("once", 1);
                },
                TimeSpan.FromMinutes(5));
        }

        Assert.Equal(1, factoryCalls); // factory chamado só na primeira vez
    }

    // ── Fire-safe: nunca propaga exceção ──────────────────────────────────────

    [Fact]
    public async Task GetAsync_WhenCacheThrows_ReturnsNull()
    {
        var cache = CreateCache(new ThrowingDistributedCache());
        var result = await cache.GetAsync<SampleDto>("any-key");
        Assert.Null(result); // swallowed, não propagou
    }

    [Fact]
    public async Task SetAsync_WhenCacheThrows_DoesNotThrow()
    {
        var cache = CreateCache(new ThrowingDistributedCache());
        // Deve completar silenciosamente
        await cache.SetAsync("any-key", new SampleDto("x", 0), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task RemoveAsync_WhenCacheThrows_DoesNotThrow()
    {
        var cache = CreateCache(new ThrowingDistributedCache());
        await cache.RemoveAsync("any-key");
    }

    [Fact]
    public async Task GetOrSetAsync_WhenCacheThrows_StillCallsFactory()
    {
        var cache = CreateCache(new ThrowingDistributedCache());
        var result = await cache.GetOrSetAsync(
            "fallback-key",
            async ct =>
            {
                await Task.Yield();
                return new SampleDto("fallback-value", -1);
            },
            TimeSpan.FromMinutes(5));

        // Mesmo com cache quebrado, o factory foi chamado e o resultado retornado
        Assert.Equal("fallback-value", result.Name);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private sealed record SampleDto(string Name, int Count);

    /// <summary>Simula Redis indisponível — sempre lança.</summary>
    private sealed class ThrowingDistributedCache : IDistributedCache
    {
        public byte[]?  Get(string key)                                                              => throw new InvalidOperationException("Redis down");
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)                => throw new InvalidOperationException("Redis down");
        public void     Set(string key, byte[] value, DistributedCacheEntryOptions options)          => throw new InvalidOperationException("Redis down");
        public Task     SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw new InvalidOperationException("Redis down");
        public void     Refresh(string key)                                                          => throw new InvalidOperationException("Redis down");
        public Task     RefreshAsync(string key, CancellationToken token = default)                  => throw new InvalidOperationException("Redis down");
        public void     Remove(string key)                                                           => throw new InvalidOperationException("Redis down");
        public Task     RemoveAsync(string key, CancellationToken token = default)                   => throw new InvalidOperationException("Redis down");
    }
}
