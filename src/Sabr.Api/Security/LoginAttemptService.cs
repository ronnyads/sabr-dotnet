using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Phub.Api.Options;

namespace Phub.Api.Security;

public sealed class LoginAttemptService
{
    private readonly IMemoryCache _cache;
    private readonly LoginProtectionOptions.CredentialLockSettings _settings;

    public LoginAttemptService(
        IMemoryCache cache,
        IOptions<LoginProtectionOptions> loginProtectionOptions)
    {
        _cache = cache;
        _settings = loginProtectionOptions.Value.CredentialLock ?? new LoginProtectionOptions.CredentialLockSettings();
    }

    public bool TryGetLock(string realm, string email, string ip, out int retryAfterSeconds)
    {
        if (!_settings.Enabled)
        {
            retryAfterSeconds = 0;
            return false;
        }

        var lockKey = BuildLockKey(realm, email, ip);
        if (_cache.TryGetValue<DateTimeOffset>(lockKey, out var lockedUntil))
        {
            var remaining = lockedUntil - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                return true;
            }
        }

        retryAfterSeconds = 0;
        return false;
    }

    public void RegisterFailure(string realm, string email, string ip)
    {
        if (!_settings.Enabled)
        {
            return;
        }

        var maxAttempts = Math.Max(1, _settings.MaxAttempts);
        var attemptWindow = TimeSpan.FromMinutes(Math.Max(1, _settings.AttemptWindowMinutes));
        var lockDuration = TimeSpan.FromMinutes(Math.Max(1, _settings.LockDurationMinutes));

        var attemptsKey = BuildAttemptsKey(realm, email, ip);
        var attempts = _cache.GetOrCreate(attemptsKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = attemptWindow;
            return 0;
        });

        attempts++;
        _cache.Set(attemptsKey, attempts, attemptWindow);

        if (attempts >= maxAttempts)
        {
            _cache.Set(BuildLockKey(realm, email, ip), DateTimeOffset.UtcNow.Add(lockDuration), lockDuration);
            _cache.Remove(attemptsKey);
        }
    }

    public void RegisterSuccess(string realm, string email, string ip)
    {
        _cache.Remove(BuildAttemptsKey(realm, email, ip));
        _cache.Remove(BuildLockKey(realm, email, ip));
    }

    private static string BuildAttemptsKey(string realm, string email, string ip)
        => $"login:attempts:{realm}:{email.ToLowerInvariant()}:{ip}";

    private static string BuildLockKey(string realm, string email, string ip)
        => $"login:lock:{realm}:{email.ToLowerInvariant()}:{ip}";
}
