namespace Phub.Domain.Enums;

public enum ClientStatus
{
    PendingProfile = 0,
    PendingAdminApproval = 1,
    PendingDocuments = 2,
    UnderReview = 3,
    Approved = 4,
    Rejected = 5,
    Inactive = 6
}
