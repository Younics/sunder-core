namespace Sunder.Sdk.Abstractions;

public interface IPackageSecrets
{
    string? GetSecret(string key);

    void SetSecret(string key, string value);

    void DeleteSecret(string key);
}
