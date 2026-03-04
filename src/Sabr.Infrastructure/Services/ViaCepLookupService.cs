using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Validation;

namespace Sabr.Infrastructure.Services;

public sealed class ViaCepLookupService : ICepLookup
{
    private const int CacheDays = 14;
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ViaCepLookupService> _logger;

    public ViaCepLookupService(HttpClient httpClient, IMemoryCache cache, ILogger<ViaCepLookupService> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _logger = logger;
    }

    public async Task<CepLookupResult> LookupAsync(string cep, CancellationToken cancellationToken = default)
    {
        var normalized = BrazilValidators.OnlyDigits(cep);
        if (normalized.Length != 8)
        {
            return new CepLookupResult(CepLookupStatus.NotFound);
        }

        var cacheKey = $"viacep:{normalized}";
        if (_cache.TryGetValue(cacheKey, out CepLookupResult? cached) && cached != null)
        {
            return cached;
        }

        try
        {
            using var response = await _httpClient.GetAsync($"ws/{normalized}/json/", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("ViaCEP returned {StatusCode} for CEP {Cep}", response.StatusCode, normalized);
                return new CepLookupResult(CepLookupStatus.Unavailable);
            }

            var payload = await response.Content.ReadFromJsonAsync<ViaCepResponse>(cancellationToken: cancellationToken);
            if (payload == null)
            {
                return new CepLookupResult(CepLookupStatus.Unavailable);
            }

            CepLookupResult result;
            if (payload.Erro == true)
            {
                result = new CepLookupResult(CepLookupStatus.NotFound);
            }
            else
            {
                result = new CepLookupResult(
                    CepLookupStatus.Found,
                    payload.Logradouro,
                    payload.Bairro,
                    payload.Localidade,
                    payload.Uf,
                    payload.Complemento);
            }

            _cache.Set(cacheKey, result, TimeSpan.FromDays(CacheDays));
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ViaCEP lookup failed for CEP {Cep}", normalized);
            return new CepLookupResult(CepLookupStatus.Unavailable);
        }
    }

    private sealed class ViaCepResponse
    {
        [JsonPropertyName("logradouro")] public string? Logradouro { get; set; }
        [JsonPropertyName("bairro")] public string? Bairro { get; set; }
        [JsonPropertyName("localidade")] public string? Localidade { get; set; }
        [JsonPropertyName("uf")] public string? Uf { get; set; }
        [JsonPropertyName("complemento")] public string? Complemento { get; set; }
        [JsonPropertyName("erro")] public bool? Erro { get; set; }
    }
}
