namespace Phub.Application.Models;

public sealed class DocumentLookupResult
{
    public string PersonType { get; set; } = "pj"; // "pj" ou "pf"
    public string? LegalName { get; set; }
    public string? TradeName { get; set; }
    public string? StateRegistration { get; set; }
    public bool IsStateRegistrationExempt { get; set; }

    public DocumentAddress? Address { get; set; }
}

public sealed class DocumentAddress
{
    public string? ZipCode { get; set; }
    public string? Street { get; set; }
    public string? Number { get; set; }
    public string? District { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Complement { get; set; }
}
