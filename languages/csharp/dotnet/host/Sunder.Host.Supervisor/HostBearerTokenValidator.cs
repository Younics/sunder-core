using System.Security.Cryptography;
using System.Text;

namespace Sunder.Host.Supervisor;

internal sealed class HostBearerTokenValidator
{
    private readonly byte[] _expectedHash;

    public HostBearerTokenValidator(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Host bearer token is required.", nameof(token));
        }
        _expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    public bool IsValid(string? authorization)
    {
        const string prefix = "Bearer ";
        if (string.IsNullOrWhiteSpace(authorization)
            || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = authorization[prefix.Length..].Trim();
        if (token.Length is 0 or > 1024)
        {
            return false;
        }
        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return CryptographicOperations.FixedTimeEquals(_expectedHash, actualHash);
    }
}
