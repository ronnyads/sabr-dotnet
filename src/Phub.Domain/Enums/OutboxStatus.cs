namespace Phub.Domain.Enums;

public enum OutboxStatus
{
    Pending = 1,
    Processing = 2,
    Retry = 3,
    Processed = 4,
    DeadLetter = 5
}
