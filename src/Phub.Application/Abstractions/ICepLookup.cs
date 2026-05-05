using Phub.Application.Models;

namespace Phub.Application.Abstractions;

public interface ICepLookup
{
    Task<CepLookupResult> LookupAsync(string cep, CancellationToken cancellationToken = default);
}
