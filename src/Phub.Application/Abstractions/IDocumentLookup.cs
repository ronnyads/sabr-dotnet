using Phub.Application.Models;

namespace Phub.Application.Abstractions;

public interface IDocumentLookup
{
    Task<DocumentLookupResult?> LookupAsync(string documentDigits, CancellationToken cancellationToken = default);
}
