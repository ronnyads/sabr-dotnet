using Sabr.Application.Models;

namespace Sabr.Application.Abstractions;

public interface IDocumentLookup
{
    Task<DocumentLookupResult?> LookupAsync(string documentDigits, CancellationToken cancellationToken = default);
}
