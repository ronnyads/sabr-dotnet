namespace Phub.Domain.Enums;

public enum SupplierWalletEntryStatus
{
    Pending = 0,
    Blocked = 1,
    Available = 2,
    Withdrawing = 3,
    Withdrawn = 4,
    Refunded = 5,
    Reversed = 6
}
