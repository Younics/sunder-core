using System.Security.Cryptography;
using System.Text;

namespace Sunder.Runtime.Host;

internal sealed class RuntimeBearerTokenValidator
{
    private readonly byte[] _expectedHash;

    public RuntimeBearerTokenValidator(string bearerToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            throw new ArgumentException("Runtime bearer token is required.", nameof(bearerToken));
        }

        _expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(bearerToken));
    }

    public bool IsValid(string? authorizationHeader)
    {
        const string prefix = "Bearer ";
        if (string.IsNullOrWhiteSpace(authorizationHeader)
            || !authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(authorizationHeader[prefix.Length..].Trim()));
        return CryptographicOperations.FixedTimeEquals(_expectedHash, candidateHash);
    }
}
