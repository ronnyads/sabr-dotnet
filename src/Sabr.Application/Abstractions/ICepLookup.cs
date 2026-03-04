using Sabr.Application.Models;

namespace Sabr.Application.Abstractions;

public interface ICepLookup
{
    Task<CepLookupResult> LookupAsync(string cep, CancellationToken cancellationToken = default);
}
