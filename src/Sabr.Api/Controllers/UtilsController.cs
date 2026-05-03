using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;

namespace Phub.Api.Controllers;

[ApiController]
[Route("api/v1/utils")]
public sealed class UtilsController : ControllerBase
{
    private const int CepRateLimitPerMinute = 30;
    private readonly ICepLookup _cepLookup;
    private readonly IMemoryCache _cache;

    public UtilsController(ICepLookup cepLookup, IMemoryCache cache)
    {
        _cepLookup = cepLookup;
        _cache = cache;
    }

    [HttpGet("cep/{cep}")]
    public async Task<IActionResult> GetCep(string cep, CancellationToken cancellationToken)
    {
        var normalized = BrazilValidators.OnlyDigits(cep);
        if (normalized.Length != 8)
        {
            return BadRequest(new { error = "CEP deve conter 8 digitos" });
        }

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!ConsumeRateLimit(ip))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Muitas requisicoes. Tente novamente em instantes." });
        }

        var result = await _cepLookup.LookupAsync(normalized, cancellationToken);
        return result.Status switch
        {
            CepLookupStatus.Found => Ok(new
            {
                cep = normalized,
                street = result.Street,
                district = result.District,
                city = result.City,
                state = result.State,
                complement = result.Complement
            }),
            CepLookupStatus.NotFound => NotFound(new { error = "CEP inexistente" }),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Servico de CEP indisponivel" })
        };
    }

    private bool ConsumeRateLimit(string ip)
    {
        var key = $"cep-rate:{ip}";
        var now = DateTimeOffset.UtcNow;
        var entry = _cache.Get<RateLimitEntry>(key);

        if (entry == null || entry.WindowStart.AddMinutes(1) <= now)
        {
            _cache.Set(key, new RateLimitEntry(now, 1), TimeSpan.FromMinutes(1));
            return true;
        }

        if (entry.Count >= CepRateLimitPerMinute)
        {
            return false;
        }

        entry.Count += 1;
        _cache.Set(key, entry, entry.WindowStart.AddMinutes(1) - now);
        return true;
    }

    private sealed class RateLimitEntry
    {
        public RateLimitEntry(DateTimeOffset windowStart, int count)
        {
            WindowStart = windowStart;
            Count = count;
        }

        public DateTimeOffset WindowStart { get; }
        public int Count { get; set; }
    }
}
