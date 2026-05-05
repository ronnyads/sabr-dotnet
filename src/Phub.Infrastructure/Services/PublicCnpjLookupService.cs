using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Phub.Application.Abstractions;
using Phub.Application.Exceptions;
using Phub.Application.Models;
using Phub.Application.Options;

namespace Phub.Infrastructure.Services;

public sealed class PublicCnpjLookupService : IDocumentLookup
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly PublicCnpjOptions _options;
    private readonly ILogger<PublicCnpjLookupService> _logger;

    public PublicCnpjLookupService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptions<PublicCnpjOptions> options,
        ILogger<PublicCnpjLookupService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DocumentLookupResult?> LookupAsync(string documentDigits, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"cnpj:{documentDigits}";
        if (_cache.TryGetValue(cacheKey, out DocumentLookupResult? cached) && cached != null)
        {
            return cached;
        }

        var client = _httpClientFactory.CreateClient("PublicCnpj");
        HttpResponseMessage response;

        try
        {
            response = await client.GetAsync($"/api/cnpj/v1/{documentDigits}", cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "[PublicCnpj] Falha ao contactar BrasilAPI para CNPJ {Cnpj}", documentDigits);
            throw new ExternalServiceUnavailableException("BrasilAPI indisponível", ex);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests || !response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[PublicCnpj] BrasilAPI retornou {StatusCode} para CNPJ {Cnpj}", response.StatusCode, documentDigits);
            throw new ExternalServiceUnavailableException($"BrasilAPI respondeu {(int)response.StatusCode}");
        }

        var payload = await response.Content.ReadFromJsonAsync<BrasilApiCnpjResponse>(cancellationToken: cancellationToken);
        if (payload == null)
        {
            return null;
        }

        var result = new DocumentLookupResult
        {
            PersonType = "pj",
            LegalName = payload.razao_social,
            TradeName = payload.nome_fantasia,
            StateRegistration = payload.inscricao_estadual,
            IsStateRegistrationExempt = string.Equals(payload.inscricao_estadual, "ISENTO", StringComparison.OrdinalIgnoreCase),
            Address = new DocumentAddress
            {
                ZipCode = payload.cep,
                Street = payload.logradouro,
                Number = payload.numero,
                District = payload.bairro,
                City = payload.municipio,
                State = payload.uf,
                Complement = payload.complemento
            }
        };

        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(_options.CacheMinutes));
        return result;
    }

    private sealed class BrasilApiCnpjResponse
    {
        public string? razao_social { get; set; }
        public string? nome_fantasia { get; set; }
        public string? cnpj { get; set; }
        public string? cep { get; set; }
        public string? logradouro { get; set; }
        public string? numero { get; set; }
        public string? bairro { get; set; }
        public string? municipio { get; set; }
        public string? uf { get; set; }
        public string? complemento { get; set; }
        public string? inscricao_estadual { get; set; }
    }
}
