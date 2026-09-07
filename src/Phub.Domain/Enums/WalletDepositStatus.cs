namespace Phub.Domain.Enums;

public enum WalletDepositStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Cancelled = 4
}

public enum WalletDepositMethod
{
    BankTransfer = 1,
    Pix = 2
}
