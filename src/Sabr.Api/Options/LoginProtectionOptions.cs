namespace Sabr.Api.Options;

public sealed class LoginProtectionOptions
{
    public const string SectionName = "LoginProtection";

    public IpRateLimitSettings IpRateLimit { get; set; } = new();
    public CredentialLockSettings CredentialLock { get; set; } = new();

    public sealed class IpRateLimitSettings
    {
        public bool Enabled { get; set; } = true;
        public int PermitLimit { get; set; } = 10;
        public int WindowSeconds { get; set; } = 60;
        public int QueueLimit { get; set; } = 0;
    }

    public sealed class CredentialLockSettings
    {
        public bool Enabled { get; set; } = true;
        public int MaxAttempts { get; set; } = 5;
        public int AttemptWindowMinutes { get; set; } = 10;
        public int LockDurationMinutes { get; set; } = 15;
    }
}
