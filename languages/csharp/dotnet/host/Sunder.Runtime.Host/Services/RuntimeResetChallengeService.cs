using System.Security.Cryptography;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeResetChallengeService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private string? _challenge;
    private DateTimeOffset _expiresAtUtc;

    public (string Challenge, DateTimeOffset ExpiresAtUtc) Create()
    {
        lock (_gate)
        {
            _challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            _expiresAtUtc = DateTimeOffset.UtcNow + Lifetime;
            return (_challenge, _expiresAtUtc);
        }
    }

    public bool TryConsume(string? challenge)
    {
        lock (_gate)
        {
            var expected = _challenge;
            _challenge = null;
            if (expected is null
                || string.IsNullOrWhiteSpace(challenge)
                || challenge.Length != expected.Length
                || DateTimeOffset.UtcNow > _expiresAtUtc)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(expected),
                System.Text.Encoding.ASCII.GetBytes(challenge));
        }
    }
}
