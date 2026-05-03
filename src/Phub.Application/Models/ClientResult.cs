using Phub.Domain.Enums;

namespace Phub.Application.Models;

public sealed class ClientResult
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string? TenantSlug { get; set; }
    public string ProtheusCode { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;

    public PersonType PersonType { get; set; }
    public string? LegalName { get; set; }
    public string? TradeName { get; set; }
    public string? Document { get; set; }
    public string? StateRegistration { get; set; }
    public bool IsStateRegistrationExempt { get; set; }

    public string Email { get; set; } = string.Empty;
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

    public ClientStatus Status { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
    public string? RejectionReason { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTimeOffset? ProfileCompletedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    public bool IsActive { get; set; } = true;
}
