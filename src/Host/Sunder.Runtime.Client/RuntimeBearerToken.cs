using System.Security.Cryptography;

namespace Sunder.Runtime.Client;

public static class RuntimeBearerToken
{
    public static string Create()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
