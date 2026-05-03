using Phub.Domain.Enums;

namespace Phub.Application.Models;

public sealed class ClientPublicRegisterRequest
{
    public PersonType PersonType { get; set; } = PersonType.CNPJ;

    public string LegalName { get; set; } = string.Empty;
    public string? TradeName { get; set; }
    public string Document { get; set; } = string.Empty;
    public string? StateRegistration { get; set; }
    public bool IsStateRegistrationExempt { get; set; }

    public string Email { get; set; } = string.Empty;
    public string Whatsapp { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public DateTime? BirthDate { get; set; }

    public string ZipCode { get; set; } = string.Empty;
    public string Street { get; set; } = string.Empty;
    public string Number { get; set; } = string.Empty;
    public string District { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? Complement { get; set; }

    public string ResponsibleName { get; set; } = string.Empty;
    public string ResponsibleDocument { get; set; } = string.Empty;
}
