using Phub.Domain.Common;
using Phub.Domain.Enums;

namespace Phub.Domain.Entities;

public sealed class Client : EntityBase
{
    public PersonType PersonType { get; set; } = PersonType.CNPJ;

    public string ProtheusCode { get; set; } = string.Empty;

    public string AccountName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool MustChangePassword { get; set; } = true;
    public DateTimeOffset? ProfileCompletedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    public string? LegalName { get; set; }
    public string? TradeName { get; set; }
    public string? Document { get; set; }
    public string? StateRegistration { get; set; }
    public bool IsStateRegistrationExempt { get; set; }

    public string? Whatsapp { get; set; }
    public string? Phone { get; set; }
    public DateTime? BirthDate { get; set; }

    public string? ZipCode { get; set; }
    public string? Street { get; set; }
    public string? Number { get; set; }
    public string? District { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Complement { get; set; }

    public string? ResponsibleName { get; set; }
    public string? ResponsibleDocument { get; set; }

    // Last wizard step the client was on (0..3). Used to resume onboarding after leaving.
    public int? OnboardingStep { get; set; }

    public ClientStatus Status { get; set; } = ClientStatus.PendingProfile;
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
    public string? RejectionReason { get; set; }

    public ICollection<ClientDocument> Documents { get; set; } = new List<ClientDocument>();
    public ICollection<ClientStore> Stores { get; set; } = new List<ClientStore>();
}
