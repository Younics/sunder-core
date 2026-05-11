using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

[SunderSdkCapability(SunderSdkCapabilities.SecretsV1)]
public interface IPackageSecrets
{
    string? GetSecret(string key);

    void SetSecret(string key, string value);

    void DeleteSecret(string key);
}
