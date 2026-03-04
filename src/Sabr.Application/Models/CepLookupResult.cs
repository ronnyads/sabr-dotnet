namespace Sabr.Application.Models;

public sealed record CepLookupResult(
    CepLookupStatus Status,
    string? Street = null,
    string? District = null,
    string? City = null,
    string? State = null,
    string? Complement = null
);
